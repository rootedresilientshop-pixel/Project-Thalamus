import json
import os
import sys


BRIDGEMOD_PATH = r"D:\Projects\BridgeMod"
KANON_SENTRY_REQUIRED_VERSION = "1.0.1"
SENTRY_KEY_FILE = "sentry.key"
PULSE_AUTH_FILE = "pulse.auth"


def ensure_audit_log() -> str:
    base_dir = os.path.dirname(os.path.abspath(__file__))
    data_dir = os.path.join(base_dir, "Data")
    log_path = os.path.join(data_dir, "kanon_audit.log")
    os.makedirs(data_dir, exist_ok=True)
    if not os.path.exists(log_path):
        with open(log_path, "a", encoding="utf-8"):
            pass
    return log_path


def warn_seed_if_missing() -> None:
    if not os.environ.get("KANON_SEED"):
        print("[DreamCraft Studio] Sentry Seed not found. Please run the Setup Guide.")


def _build_manifest(hardware_id: str) -> dict:
    return {
        "ModSurfaces": [{"Name": f"HWID:{hardware_id}", "Weight": 1.0}],
        "WeightTables": [{"Key": "HWID", "Value": 1.0}],
    }


def _read_key_file(path: str) -> str:
    if not os.path.exists(path):
        return ""
    try:
        return open(path, "r", encoding="utf-8").read().strip()
    except Exception:
        return ""


def _sentry_unlock(seed: str) -> bool:
    if not seed:
        return False
    os.environ["KANON_SEED"] = seed
    try:
        from importlib import metadata
        installed_version = metadata.version("kanon-sentry")
        if installed_version != KANON_SENTRY_REQUIRED_VERSION:
            return False
    except Exception:
        return False

    try:
        import kanon_sentry  # type: ignore
        ks_core = getattr(kanon_sentry, "kanon_core", None)
        return ks_core is not None
    except Exception:
        return False


def _pulse_unlock() -> bool:
    try:
        if BRIDGEMOD_PATH not in sys.path:
            sys.path.insert(0, BRIDGEMOD_PATH)
        import bridgemod  # type: ignore
        _ = bridgemod.get_hardware_id()
        return True
    except Exception:
        return False


def main() -> int:
    ensure_audit_log()
    warn_seed_if_missing()
    base_dir = os.path.dirname(os.path.abspath(__file__))
    data_dir = os.path.join(base_dir, "Data")
    sentry_key_path = os.path.join(data_dir, SENTRY_KEY_FILE)
    pulse_auth_path = os.path.join(data_dir, PULSE_AUTH_FILE)

    sentry_seed = _read_key_file(sentry_key_path)
    pulse_auth = _read_key_file(pulse_auth_path)

    sentry_unlocked = _sentry_unlock(sentry_seed) if sentry_seed else False
    pulse_unlocked = _pulse_unlock() if pulse_auth else False

    if sentry_seed and not sentry_unlocked:
        print("[DreamCraft Studio] Sentry key detected but unlock failed.")
    if pulse_auth and not pulse_unlocked:
        print("[DreamCraft Studio] Pulse auth detected but handshake failed.")

    # Lite Mode: if neither key exists, remain functional with locked Sovereign features.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
