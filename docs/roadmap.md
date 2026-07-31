# Roadmap

1. Validate the framework with `doctor`, snapshots, schema probing, and helper
   protocol tests.
2. Receive verified `message_0.db` and `media_0.db` schema JSON, WeChat
   version, and a few known voice timestamps.
3. Implement one isolated schema adapter for a specified contact, incoming
   voice records, original SILK output, and a manifest.
4. Evaluate a local SILK decoder and only then consider a UI.
