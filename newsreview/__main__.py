"""Run with python -m newsreview. No package installation required."""

import argparse
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import threading

from . import __version__
from .collector import Collector
from .server import AppServer
from .store import Store
from .migration import database_path


def main():
    parser = argparse.ArgumentParser(description="NewsReview: self-hosted news reader with local AI")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8714)
    parser.add_argument("--data-dir", default=os.environ.get("NEWSREVIEW_DATA_DIR", "data"))
    parser.add_argument("--no-scheduler", action="store_true", help="Disable automatic collection for this process")
    args = parser.parse_args()
    password = os.environ.get("NEWSREVIEW_PASSWORD", "")
    if not 1 <= args.port <= 65535:
        parser.error("Port must be 1–65535")
    directory = Path(args.data_dir).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(directory / "server.log", maxBytes=5_000_000, backupCount=1, encoding="utf-8")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s", handlers=[logging.StreamHandler(), handler])
    store = Store(database_path(directory))
    collector = Collector(store)
    server = AppServer((args.host, args.port), store, collector, password)
    if server.auth.setup_token:
        print(f"\nNewsReview first-run setup token: {server.auth.setup_token}\nOpen the app and enter this token to create your password.\nThe token changes on restart until setup is complete.\n", flush=True)
    if not args.no_scheduler:
        threading.Thread(target=collector.schedule, daemon=True, name="collection-scheduler").start()
    logging.info("NewsReview %s at http://%s:%s · data: %s", __version__, args.host, args.port, directory)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logging.info("Stopping")
    finally:
        collector.stop()
        server.server_close()


if __name__ == "__main__":
    main()
