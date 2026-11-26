#!/usr/bin/env python3
import argparse
import json
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--path", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--target-abi", required=True)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--timestamp", required=True)
    parser.add_argument("--changelog", required=True)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    metadata_file = Path(__file__).resolve().parent.parent / "build.yaml"
    metadata = json.loads(metadata_file.read_text(encoding="utf-8"))
    plugin = {key: metadata[key] for key in ("guid", "name", "overview", "description", "owner", "category")}
    previous = json.loads(args.path.read_text(encoding="utf-8")) if args.path.exists() else []
    plugin["versions"] = next((item["versions"] for item in previous if item["guid"] == plugin["guid"]), [])

    plugin["versions"] = [
        version for version in plugin["versions"] if version.get("version") != args.version
    ]
    plugin["versions"].insert(
        0,
        {
            "version": args.version,
            "changelog": args.changelog,
            "targetAbi": args.target_abi,
            "sourceUrl": args.source_url,
            "checksum": args.checksum,
            "timestamp": args.timestamp,
        },
    )
    args.path.write_text(json.dumps([plugin], indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
