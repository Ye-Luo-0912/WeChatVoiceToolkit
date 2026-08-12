"""Small, non-Gradio Seed-VC v1 bridge used by WeChatVoiceToolkit.

The upstream checkout remains the source of the model implementation. This
bridge only loads app_vc's model helpers and writes the final WAV produced by
the same conversion generator used by the official app. It deliberately has
no network or shell behavior; model downloads, if requested by upstream, are
still controlled by the user's Seed-VC environment.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--diffusion-steps", type=int, default=50)
    parser.add_argument("--length-adjust", type=float, default=1.0)
    parser.add_argument("--inference-cfg-rate", type=float, default=0.7)
    parser.add_argument("--fp16", type=lambda value: value.lower() == "true", default=True)
    args = parser.parse_args()

    # The bridge is executed with the Seed-VC checkout as cwd. Keeping the
    # import path explicit prevents accidentally importing a different app_vc.
    root = Path.cwd().resolve()
    sys.path.insert(0, str(root))
    import numpy as np  # type: ignore
    import torch  # type: ignore
    import torchaudio  # type: ignore
    import app_vc  # type: ignore

    app_vc.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model_args = argparse.Namespace(
        checkpoint=args.checkpoint,
        config=args.config,
        fp16=args.fp16,
        gpu=0,
    )
    (
        app_vc.model,
        app_vc.semantic_fn,
        app_vc.vocoder_fn,
        app_vc.campplus_model,
        app_vc.to_mel,
        app_vc.mel_fn_args,
    ) = app_vc.load_models(model_args)
    app_vc.max_context_window = app_vc.sr // app_vc.hop_length * 30
    app_vc.overlap_wave_len = app_vc.overlap_frame_len * app_vc.hop_length

    final_audio = None
    for _, payload in app_vc.voice_conversion(
        args.source,
        args.reference,
        args.diffusion_steps,
        args.length_adjust,
        args.inference_cfg_rate,
    ):
        if payload is not None:
            final_audio = payload
    if final_audio is None:
        raise RuntimeError("Seed-VC produced no final audio")
    sample_rate, samples = final_audio
    waveform = torch.from_numpy(np.asarray(samples, dtype=np.float32)).unsqueeze(0)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    torchaudio.save(str(output), waveform, int(sample_rate), encoding="PCM_S", bits_per_sample=16)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
