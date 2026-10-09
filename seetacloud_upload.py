#!/usr/bin/env python3
"""Upload files over SFTP with progress output."""

import argparse
import os
import time

import paramiko


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--user", required=True)
    parser.add_argument("--file", action="append", nargs=2, metavar=("LOCAL", "REMOTE"))
    args = parser.parse_args()

    transport = paramiko.Transport((args.host, args.port))
    transport.connect(username=args.user, password=os.environ["SEETACLOUD_PASSWORD"])
    transport.set_keepalive(30)
    sftp = paramiko.SFTPClient.from_transport(transport)

    try:
        for local_path, remote_path in args.file:
            started = time.monotonic()
            last_report = [0.0]

            def progress(transferred, total):
                now = time.monotonic()
                if now - last_report[0] >= 5 or transferred == total:
                    elapsed = max(now - started, 0.001)
                    mib = transferred / 1024 / 1024
                    rate = mib / elapsed
                    print(
                        f"{local_path}: {mib:.1f}/{total / 1024 / 1024:.1f} MiB "
                        f"({transferred / total * 100:.1f}%) {rate:.1f} MiB/s",
                        flush=True,
                    )
                    last_report[0] = now

            sftp.put(local_path, remote_path, callback=progress, confirm=True)
            print(f"UPLOADED {local_path} -> {remote_path}", flush=True)
    finally:
        sftp.close()
        transport.close()


if __name__ == "__main__":
    main()
