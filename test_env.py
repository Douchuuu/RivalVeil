"""
Environment variables test
"""
import os
from dotenv import load_dotenv

print("=" * 50)
print("Environment Variables Test")
print("=" * 50)

# Before load_dotenv
print("\n--- Before load_dotenv() ---")
print(f"DB_PASSWORD from os.environ: {os.environ.get('DB_PASSWORD', 'NOT SET')}")

# Load .env
load_dotenv()

print("\n--- After load_dotenv() ---")
print(f"DB_HOST: {os.getenv('DB_HOST', 'localhost')}")
print(f"DB_USER: {os.getenv('DB_USER', 'root')}")
print(f"DB_PASSWORD: {'*' * len(os.getenv('DB_PASSWORD', '')) if os.getenv('DB_PASSWORD') else 'NOT SET'}")
print(f"DB_NAME: {os.getenv('DB_NAME', 'rivalveil')}")
print(f"GIST_TOKEN: {'*' * 10}..." if os.getenv('GIST_TOKEN') else 'GIST_TOKEN: NOT SET')

# Show raw .env file content (masked)
print("\n--- Raw .env content (first 20 lines) ---")
try:
    with open('.env', 'r') as f:
        for i, line in enumerate(f):
            if i >= 20:
                break
            line = line.strip()
            if line and not line.startswith('#'):
                if 'PASSWORD' in line or 'TOKEN' in line:
                    key = line.split('=')[0] if '=' in line else line
                    print(f"{key}=***HIDDEN***")
                else:
                    print(line)
except FileNotFoundError:
    print("❌ .env file not found!")

input("\nPress Enter to exit...")
