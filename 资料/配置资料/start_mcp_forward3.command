#!/bin/bash
# Dify MCP SSE TCP Forwarder for macOS
# Forward: Mac 0.0.0.0:15001 -> Windows MCP 192.168.200.10:5001
# Dify URL to use: http://host.docker.internal:15001/sse

set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LISTEN_HOST="0.0.0.0"
LISTEN_PORT="15001"
TARGET_HOST="192.168.200.10"
TARGET_PORT="5001"
LOG_DIR="${SCRIPT_DIR}/mcp_forward_logs"
LOG_RETENTION_DAYS="30"
TERMINAL_MAX_LINES="1000"

clear
echo "============================================================"
echo " Dify MCP SSE Forwarder"
echo "============================================================"
echo "Forwarding: ${LISTEN_HOST}:${LISTEN_PORT}  ->  ${TARGET_HOST}:${TARGET_PORT}"
echo "Dify MCP URL: http://host.docker.internal:${LISTEN_PORT}/sse"
echo "Log dir: ${LOG_DIR} (keep ${LOG_RETENTION_DAYS} days)"
echo "Terminal: clear after ${TERMINAL_MAX_LINES} lines"
echo "Stop: press Ctrl+C in this terminal"
echo "============================================================"
echo

if ! command -v python3 >/dev/null 2>&1; then
  echo "ERROR: python3 not found. Install Python 3 or Xcode Command Line Tools first."
  echo "Press Enter to exit."
  read -r _
  exit 1
fi

export MCP_FWD_LISTEN_HOST="$LISTEN_HOST"
export MCP_FWD_LISTEN_PORT="$LISTEN_PORT"
export MCP_FWD_TARGET_HOST="$TARGET_HOST"
export MCP_FWD_TARGET_PORT="$TARGET_PORT"
export MCP_FWD_LOG_DIR="$LOG_DIR"
export MCP_FWD_LOG_RETENTION_DAYS="$LOG_RETENTION_DAYS"
export MCP_FWD_TERMINAL_MAX_LINES="$TERMINAL_MAX_LINES"

python3 - <<'PY'
import os
import socket
import threading
import time
import signal
import sys
import itertools
import errno

LISTEN_HOST = os.environ.get("MCP_FWD_LISTEN_HOST", "0.0.0.0")
LISTEN_PORT = int(os.environ.get("MCP_FWD_LISTEN_PORT", "15001"))
TARGET_HOST = os.environ.get("MCP_FWD_TARGET_HOST", "192.168.200.10")
TARGET_PORT = int(os.environ.get("MCP_FWD_TARGET_PORT", "5001"))
CONNECT_TIMEOUT = 10
LOG_DIR = os.environ.get("MCP_FWD_LOG_DIR", "")
LOG_RETENTION_DAYS = int(os.environ.get("MCP_FWD_LOG_RETENTION_DAYS", "30"))
TERMINAL_MAX_LINES = int(os.environ.get("MCP_FWD_TERMINAL_MAX_LINES", "1000"))
RETENTION_INTERVAL_SECONDS = 3600

stop = False
connection_ids = itertools.count(1)
log_lock = threading.Lock()
last_retention_at = 0
terminal_line_count = 0

def terminal_header():
    return [
        "============================================================",
        " Dify MCP SSE Forwarder",
        "============================================================",
        f"Forwarding: {LISTEN_HOST}:{LISTEN_PORT}  ->  {TARGET_HOST}:{TARGET_PORT}",
        f"Dify MCP URL: http://host.docker.internal:{LISTEN_PORT}/sse",
        f"Log dir: {LOG_DIR or '<disabled>'} (keep {LOG_RETENTION_DAYS} days)",
        f"Terminal: clear after {TERMINAL_MAX_LINES} lines",
        "Stop: press Ctrl+C in this terminal",
        "============================================================",
        "",
    ]

def log(message):
    global last_retention_at, terminal_line_count
    line = time.strftime("[%Y-%m-%d %H:%M:%S] ") + message
    try:
        with log_lock:
            if TERMINAL_MAX_LINES > 0 and terminal_line_count >= TERMINAL_MAX_LINES:
                render_header()
                terminal_line_count = 0
            print(line, flush=True)
            terminal_line_count += 1
            append_daily_log(line)
            now = time.monotonic()
            if now - last_retention_at >= RETENTION_INTERVAL_SECONDS:
                cleanup_old_logs()
                last_retention_at = now
    except Exception:
        print(line, flush=True)

def render_header():
    try:
        sys.stdout.write("\033[2J\033[3J\033[H")
        for header_line in terminal_header():
            sys.stdout.write(header_line + "\n")
        sys.stdout.flush()
    except Exception:
        pass

render_header()

def append_daily_log(line):
    if not LOG_DIR:
        return
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        log_path = os.path.join(LOG_DIR, time.strftime("%Y-%m-%d") + ".log")
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass

def cleanup_old_logs():
    if not LOG_DIR or LOG_RETENTION_DAYS <= 0:
        return
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        cutoff = time.time() - LOG_RETENTION_DAYS * 86400
        for name in os.listdir(LOG_DIR):
            if not name.endswith(".log"):
                continue
            path = os.path.join(LOG_DIR, name)
            try:
                if os.path.isfile(path) and os.path.getmtime(path) < cutoff:
                    os.remove(path)
            except Exception:
                pass
    except Exception:
        pass

def handle_signal(signum, frame):
    global stop
    stop = True
    log("Stopping forwarder...")
    sys.exit(0)

signal.signal(signal.SIGINT, handle_signal)
signal.signal(signal.SIGTERM, handle_signal)

def configure_stream_socket(sock):
    sock.settimeout(None)
    try:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    except Exception:
        pass
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
    except Exception:
        pass

def shutdown_write(sock):
    try:
        sock.shutdown(socket.SHUT_WR)
    except Exception:
        pass

def close_socket(sock):
    try:
        sock.shutdown(socket.SHUT_RDWR)
    except Exception:
        pass
    try:
        sock.close()
    except Exception:
        pass

def pipe(src, dst, tag, conn_id):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                log(f"#{conn_id} {tag} closed by peer")
                shutdown_write(dst)
                break
            dst.sendall(data)
    except OSError as e:
        if e.errno in (errno.EBADF, errno.ENOTCONN):
            log(f"#{conn_id} {tag} closed")
        else:
            log(f"#{conn_id} {tag} stopped: {type(e).__name__}: {e}")
    except Exception as e:
        log(f"#{conn_id} {tag} stopped: {type(e).__name__}: {e}")

def handle(client, addr):
    conn_id = next(connection_ids)
    configure_stream_socket(client)
    log(f"#{conn_id} Client connected from {addr[0]}:{addr[1]}")
    try:
        target = socket.create_connection((TARGET_HOST, TARGET_PORT), timeout=CONNECT_TIMEOUT)
        configure_stream_socket(target)
        log(f"#{conn_id} Connected to target {TARGET_HOST}:{TARGET_PORT}")
    except Exception as e:
        log(f"#{conn_id} ERROR: connect target failed: {e}")
        try:
            client.close()
        except Exception:
            pass
        return

    t1 = threading.Thread(target=pipe, args=(client, target, "client->target", conn_id), daemon=True)
    t2 = threading.Thread(target=pipe, args=(target, client, "target->client", conn_id), daemon=True)
    t1.start()
    t2.start()
    t1.join()
    t2.join()
    close_socket(client)
    close_socket(target)
    log(f"#{conn_id} connection finished")

# Preflight target test. Do not exit if failed; the target may start later.
try:
    test = socket.create_connection((TARGET_HOST, TARGET_PORT), timeout=3)
    test.close()
    log(f"Preflight OK: target {TARGET_HOST}:{TARGET_PORT} is reachable from Mac host")
except Exception as e:
    log(f"WARNING: target {TARGET_HOST}:{TARGET_PORT} is not reachable now: {e}")
    log("The forwarder will still start. Check Windows MCP Server if clients fail.")

with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        server.bind((LISTEN_HOST, LISTEN_PORT))
    except OSError as e:
        log(f"ERROR: cannot listen on {LISTEN_HOST}:{LISTEN_PORT}: {e}")
        log("Maybe another forwarder is already running. Close it or change LISTEN_PORT.")
        sys.exit(2)

    server.listen(100)
    log(f"Forwarder started: {LISTEN_HOST}:{LISTEN_PORT} -> {TARGET_HOST}:{TARGET_PORT}")
    log(f"Use this Dify MCP URL: http://host.docker.internal:{LISTEN_PORT}/sse")

    while not stop:
        try:
            client, addr = server.accept()
        except KeyboardInterrupt:
            break
        except Exception as e:
            log(f"accept failed: {e}")
            continue
        threading.Thread(target=handle, args=(client, addr), daemon=True).start()
PY

echo
echo "Forwarder stopped. Press Enter to close this window."
read -r _
