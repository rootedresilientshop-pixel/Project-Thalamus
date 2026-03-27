import json
import sys


BRIDGEMOD_PATH = r"D:\Projects\BridgeMod"
if BRIDGEMOD_PATH not in sys.path:
    sys.path.insert(0, BRIDGEMOD_PATH)


def _try_import_kanon_sentry():
    try:
        import kanon_sentry  # type: ignore

        return kanon_sentry, None
    except Exception as exc:
        return None, exc


def _build_manifest(hardware_id: str) -> dict:
    return {
        "ModSurfaces": [
            {"Name": f"HWID:{hardware_id}", "Weight": 1.0},
        ],
        "WeightTables": [
            {"Key": "HWID", "Value": 1.0},
        ],
    }


def main() -> int:
    import bridgemod
    import kanon_core

    hardware_id = bridgemod.get_hardware_id()
    kanon_sentry, ks_error = _try_import_kanon_sentry()

    print(f"BridgeMod HWID: {hardware_id}")

    # Prefer kanon_sentry if it exists, otherwise fall back to kanon_core fingerprinting.
    if kanon_sentry is not None:
        try:
            Intent = getattr(kanon_sentry, "Intent")
            HardwareBinding = getattr(kanon_sentry, "HardwareBinding")
            GovernanceCertificate = getattr(kanon_sentry, "GovernanceCertificate")
        except Exception:
            Intent = HardwareBinding = GovernanceCertificate = None

        if Intent and HardwareBinding and GovernanceCertificate:
            intent = Intent(action="hardware.sign", constraints={"hwid": hardware_id})
            binding = HardwareBinding.from_current_machine()
            cert = GovernanceCertificate.sign_with_hardware_binding(intent, binding=binding)
            verified = bool(cert.verify_current_machine())
            print("Kanon Sentry Mode: GovernanceCertificate")
            print(f"Certificate Verified: {verified}")
            if verified:
                print("GOLD: true")
                return 0
            print("GOLD: false")
            return 2

    if ks_error is not None:
        print(f"Kanon Sentry Import Error: {ks_error}")

    manifest = _build_manifest(hardware_id)
    manifest_json = json.dumps(manifest)
    fingerprint = kanon_core.generate_logic_fingerprint(manifest_json)
    verified = bool(kanon_core.verify_logic_fingerprint(manifest_json, fingerprint))

    print("Kanon Sentry Mode: kanon_core.generate_logic_fingerprint")
    print(f"Fingerprint: {fingerprint}")
    print(f"Fingerprint Verified: {verified}")
    print(f"GOLD: {str(verified).lower()}")

    return 0 if verified else 2


if __name__ == "__main__":
    raise SystemExit(main())
