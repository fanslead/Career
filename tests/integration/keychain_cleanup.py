"""Bounded cleanup and postcondition checks for isolated Career Keychain items."""

from __future__ import annotations

import subprocess
from typing import Any


SERVICE = "Career.WorkspaceVault"
ITEM_NOT_FOUND = 44


def cleanup_keychain_item(workspace_id: str | None, security_command: str = "/usr/bin/security") -> dict[str, Any]:
    if not workspace_id:
        return {"cleanup": None, "status": "skipped", "absenceVerified": None}

    result: dict[str, Any] = {
        "cleanup": False,
        "status": "failed",
        "deleteReturnCode": None,
        "verificationReturnCode": None,
        "absenceVerified": False,
    }
    errors: list[str] = []
    try:
        deleted = subprocess.run(
            [security_command, "delete-generic-password", "-s", SERVICE, "-a", workspace_id],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=5,
        )
        result["deleteReturnCode"] = deleted.returncode
        if deleted.returncode not in (0, ITEM_NOT_FOUND):
            errors.append(f"delete-generic-password returned {deleted.returncode}; expected 0 or {ITEM_NOT_FOUND}")
    except subprocess.TimeoutExpired:
        errors.append("delete-generic-password timed out after 5 seconds")
    except OSError as error:
        errors.append(f"could not run delete-generic-password ({type(error).__name__})")

    try:
        verified = subprocess.run(
            [security_command, "find-generic-password", "-s", SERVICE, "-a", workspace_id],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=5,
        )
        result["verificationReturnCode"] = verified.returncode
        result["absenceVerified"] = verified.returncode == ITEM_NOT_FOUND
        if verified.returncode != ITEM_NOT_FOUND:
            state = "item remains present" if verified.returncode == 0 else f"returned {verified.returncode}"
            errors.append(f"find-generic-password {state}; expected {ITEM_NOT_FOUND} for an absent item")
    except subprocess.TimeoutExpired:
        errors.append("find-generic-password timed out after 5 seconds")
    except OSError as error:
        errors.append(f"could not run find-generic-password ({type(error).__name__})")

    if not errors:
        result.update({"cleanup": True, "status": "passed", "absenceVerified": True})
    else:
        result["error"] = "; ".join(errors)
    return result
