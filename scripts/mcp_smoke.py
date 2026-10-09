"""Drives an MCP stdio server through initialize, tools/list, and a list of tool calls.

Usage: python mcp_smoke.py SERVER CALLS_JSON [EXPECTED_TEXT]

CALLS_JSON is a file with a list of {"name": ..., "arguments": {...}} objects. An item can
also be {"sleep": SECONDS} to wait, or {"gpu": LABEL} to print the GPU memory in use. When
EXPECTED_TEXT is given, the script fails unless the last transcribe call succeeds and its text contains
EXPECTED_TEXT, ignoring case and punctuation.
"""
import json
import re
import shutil
import subprocess
import sys
import time

server, calls_path = sys.argv[1], sys.argv[2]
expected = sys.argv[3] if len(sys.argv) > 3 else None
with open(calls_path, encoding="utf-8") as f:
    calls = json.load(f)

proc = subprocess.Popen(
    [server],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    encoding="utf-8",
)
next_id = 1


def request(method, params=None):
    global next_id
    message = {"jsonrpc": "2.0", "id": next_id, "method": method}
    if params is not None:
        message["params"] = params
    next_id += 1
    proc.stdin.write(json.dumps(message) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("The server closed stdout:\n" + proc.stderr.read())
        response = json.loads(line)
        if response.get("id") == message["id"]:
            return response


start = time.perf_counter()
init = request(
    "initialize",
    {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}},
)
print(f"initialize: {init['result']['serverInfo']} ({(time.perf_counter() - start) * 1000:.0f} ms)")
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()
print("tools:", [t["name"] for t in request("tools/list")["result"]["tools"]])

last = None
last_transcribe = None
for call in calls:
    if "sleep" in call:
        time.sleep(call["sleep"])
        continue
    if "gpu" in call:
        if shutil.which("nvidia-smi"):
            used = subprocess.run(
                ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                capture_output=True,
                text=True,
            ).stdout.strip()
            print(f"gpu {call['gpu']}: {used} MiB used")
        continue
    start = time.perf_counter()
    last = request("tools/call", {"name": call["name"], "arguments": call.get("arguments", {})})
    if call["name"] == "transcribe":
        last_transcribe = last
    elapsed = time.perf_counter() - start
    print(f"call {call['name']} ({elapsed:.2f} s): {json.dumps(last.get('result', last.get('error')))[:4000]}")

proc.stdin.close()
proc.wait(timeout=30)
print("stderr tail:", proc.stderr.read()[-3000:])

if expected is not None:
    result = (last_transcribe or {}).get("result", {})
    text = " ".join(c.get("text", "") for c in result.get("content", []))

    def normalize(s):
        return re.sub(r"[^\w ]+", "", s.lower())

    if result.get("isError") or normalize(expected) not in normalize(text):
        sys.exit(f"Expected text not found: {expected!r}")
    print("Expected text found.")
