"""
RivalVeil — url_updater.py v2.1
Reads cloudflared URL and writes to GitHub Gist.

FIXED: Added GIST_RAW_URL for client reference.
FIXED: Added URL change check before updating Gist.
FIXED: GIST_TOKEN no longer stored in code.
FIXED: Added load_dotenv() to read from .env file.
Set before running:
  Windows: set GIST_TOKEN=ghp_...
  Linux:   export GIST_TOKEN=ghp_...

Or create .env file and load via python-dotenv:
  pip install python-dotenv
  echo GIST_TOKEN=ghp_... > .env
"""

import os
import subprocess
import re
import sys
import requests

# ═══ ИСПРАВЛЕНИЕ: Загружаем переменные из .env ═══
from dotenv import load_dotenv
load_dotenv()
# ═════════════════════════════════════════════════

# GitHub configuration
GITHUB_USERNAME = "Douchuuu"
GIST_ID   = "f33d3895db2ed6966728ad38f29bc83e"
FILENAME  = "rivalveil_url.txt"

# Raw URL for clients (Unity will fetch from here)
GIST_RAW_URL = f"https://gist.githubusercontent.com/{GITHUB_USERNAME}/{GIST_ID}/raw/{FILENAME}"

# Track last URL to avoid unnecessary API calls
_last_url = None


def update_gist(url: str):
    """Updates Gist only if URL has actually changed."""
    global _last_url
    
    # Check if URL changed
    if url == _last_url:
        print(f"[url_updater] URL unchanged, skipping Gist update")
        return
    
    token = os.environ.get("GIST_TOKEN", "")
    if not token:
        print("[url_updater] ERROR: GIST_TOKEN not set! Set environment variable.")
        print("  Windows: set GIST_TOKEN=ghp_xxx")
        print("  Linux:   export GIST_TOKEN=ghp_xxx")
        return

    headers = {
        "Authorization": f"token {token}",
        "Accept": "application/vnd.github.v3+json"
    }
    data = {"files": {FILENAME: {"content": url}}}
    try:
        resp = requests.patch(
            f"https://api.github.com/gists/{GIST_ID}",
            json=data, headers=headers, timeout=10
        )
        if resp.status_code == 200:
            _last_url = url
            print(f"[url_updater] Gist updated: {url}")
            print(f"[url_updater] Client URL: {GIST_RAW_URL}")
        else:
            print(f"[url_updater] Gist error: {resp.status_code} {resp.text[:200]}")
    except Exception as e:
        print(f"[url_updater] Network error: {e}")


def main():
    print("[url_updater] Starting cloudflared tunnel...")
    print(f"[url_updater] Gist ID: {GIST_ID}")
    print(f"[url_updater] Client fetch URL: {GIST_RAW_URL}")
    print("")
    print("=" * 60)
    print("IMPORTANT: Update GITHUB_USERNAME in this script!")
    print("=" * 60)
    print("")

    process = subprocess.Popen(
        ["cloudflared", "tunnel", "--url", "http://localhost:8000"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1
    )

    url_found = False

    try:
        for line in process.stdout:
            print(line, end="", flush=True)
            if not url_found:
                match = re.search(r'https://[a-z0-9\-]+\.trycloudflare\.com', line)
                if match:
                    found_url = match.group(0)
                    print(f"\n[url_updater] URL found: {found_url}")
                    update_gist(found_url)
                    url_found = True
    except KeyboardInterrupt:
        print("\n[url_updater] Stopped by user")
    finally:
        process.terminate()
        process.wait()


if __name__ == "__main__":
    main()
