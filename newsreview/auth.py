"""Persistent password credentials and a console-only first-run setup token."""
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import threading


class Auth:
    def __init__(self, path, bootstrap_password=""):
        self.path = Path(path)
        self.lock = threading.Lock()
        self.record = None
        self.setup_token = "-".join(secrets.token_hex(2).upper() for _ in range(4))
        if self.path.exists():
            try:
                self.record = json.loads(self.path.read_text(encoding="utf-8"))
                if (self.record["version"] != 1 or self.record["iterations"] != 600000
                        or len(base64.b64decode(self.record["salt"], validate=True)) != 16
                        or len(base64.b64decode(self.record["hash"], validate=True)) != 32):
                    raise ValueError("Invalid password hash format")
            except (ValueError, KeyError, TypeError) as exc:
                raise ValueError(f"Invalid credentials file: {self.path}. Restore a valid auth.json before starting.") from exc
            self.setup_token = None
        elif bootstrap_password:
            self.setup(self.setup_token, bootstrap_password, bootstrap_password)

    @property
    def configured(self):
        return self.record is not None

    def setup(self, token, password, confirmation):
        with self.lock:
            if self.configured:
                raise FileExistsError("A password is already configured.")
            normalize = lambda value: value.replace("-", "").replace(" ", "").upper() if isinstance(value, str) else ""
            if not hmac.compare_digest(normalize(token).encode(), normalize(self.setup_token).encode()):
                raise PermissionError("Incorrect setup token.")
            if not isinstance(password, str) or not 8 <= len(password) <= 1024:
                raise ValueError("Password must contain 8–1024 characters.")
            if password != confirmation:
                raise ValueError("Passwords do not match.")
            salt = secrets.token_bytes(16)
            record = {"version": 1, "iterations": 600000, "salt": base64.b64encode(salt).decode(),
                      "hash": base64.b64encode(hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 600000)).decode()}
            self.path.parent.mkdir(parents=True, exist_ok=True)
            # Exclusive creation works on filesystems without hard-link support,
            # and prevents competing setup requests from replacing credentials.
            fd = os.open(self.path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            try:
                with os.fdopen(fd, "w", encoding="utf-8") as stream:
                    json.dump(record, stream)
                    stream.flush()
                    os.fsync(stream.fileno())
            except BaseException:
                # Remove only the incomplete file this request created. An abrupt
                # process/power loss instead leaves invalid credentials fail-closed.
                self.path.unlink(missing_ok=True)
                raise
            self.record = record
            self.setup_token = None

    def verify(self, password):
        if not self.configured or not isinstance(password, str) or len(password) > 1024:
            return False
        record = self.record
        digest = hashlib.pbkdf2_hmac("sha256", password.encode(), base64.b64decode(record["salt"]), record["iterations"])
        return hmac.compare_digest(digest, base64.b64decode(record["hash"]))
