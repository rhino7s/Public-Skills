"""独立开发版 stdio 探针；启动失败/EOF 立即报错，不输出配置或业务结果。"""
import argparse
import json
import queue
import subprocess
import threading
import time


class ProbeFailure(RuntimeError):
    def __init__(self, stage, kind, process, elapsed):
        self.summary = {"stage": stage, "kind": kind, "exitCode": process.poll(), "elapsedMs": round(elapsed * 1000)}
        super().__init__(json.dumps(self.summary))


class Probe:
    def __init__(self, command, cwd=None):
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.events = queue.Queue()
        self.stderr = []  # 仅在内存诊断，不自动回显可能含内部信息的 stderr。
        self.sequence = 0
        self.readers = [threading.Thread(target=self._read_stdout, daemon=True), threading.Thread(target=self._read_stderr, daemon=True)]
        for reader in self.readers:
            reader.start()

    def _read_stdout(self):
        try:
            for line in self.process.stdout:
                try:
                    self.events.put(("response", json.loads(line)))
                except json.JSONDecodeError:
                    self.events.put(("invalid_stdout", None))
        finally:
            self.events.put(("eof", None))

    def _read_stderr(self):
        for line in self.process.stderr:
            if sum(map(len, self.stderr)) < 8192:
                self.stderr.append(line[:2048])

    def call(self, method, params, timeout=20):
        started = time.monotonic()
        deadline = started + timeout
        self.sequence += 1
        try:
            self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params}) + "\n")
            self.process.stdin.flush()
        except (BrokenPipeError, OSError):
            raise ProbeFailure(method, "process_exited", self.process, time.monotonic() - started)
        while True:
            remaining = deadline - time.monotonic()
            try:
                if remaining <= 0:
                    raise queue.Empty
                kind, value = self.events.get(timeout=remaining)
            except queue.Empty:
                raise ProbeFailure(method, "client_deadline", self.process, time.monotonic() - started)
            if kind != "response":
                if kind == "eof":
                    try:
                        self.process.wait(timeout=1)
                    except subprocess.TimeoutExpired:
                        pass
                raise ProbeFailure(method, "process_exited" if kind == "eof" else kind,
                                   self.process, time.monotonic() - started)
            if value.get("id") == self.sequence:
                return value

    def initialize(self):
        result = self.call("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                           "clientInfo": {"name": "project-development-probe", "version": "1"}}, timeout=5)
        if "error" in result:
            raise RuntimeError("MCP initialize rejected")
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
        self.process.wait(timeout=5)
        for reader in self.readers:
            reader.join(timeout=1)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            stream.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--database")
    parser.add_argument("--sql")
    parser.add_argument("--timeout", type=float, default=20)
    args = parser.parse_args()
    probe = Probe([args.exe, "--config", args.config])
    try:
        started = time.monotonic()
        probe.initialize()
        print(json.dumps({"stage": "initialize", "elapsedMs": round((time.monotonic()-started)*1000)}), flush=True)
        if args.sql:
            started = time.monotonic()
            result = probe.call("tools/call", {"name": "execute_sql", "arguments": {"database": args.database, "sql": args.sql}}, args.timeout)
            body = result.get("result", {}).get("structuredContent", {})
            print(json.dumps({"stage": "execute_sql", "elapsedMs": round((time.monotonic()-started)*1000),
                              "success": body.get("success"), "code": body.get("code"),
                              "errorCategory": (body.get("error") or {}).get("category"),
                              "returnedRows": body.get("returnedRows")}))
        else:
            result = probe.call("tools/list", {}, timeout=5)
            print(json.dumps({"stage": "tools/list", "count": len(result.get("result", {}).get("tools", []))}))
    except ProbeFailure as error:
        print(json.dumps(error.summary), flush=True)
        raise SystemExit(1)
    finally:
        probe.close()
