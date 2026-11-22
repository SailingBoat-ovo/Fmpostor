#!/bin/sh
# InnerslothGateway 启动脚本
# 用法: ./start.sh [端口] [X-Proxy-Key]
cd "$(dirname "$0")" || exit 1
PORT="${1:-58080}"
KEY="${2:-}"
if [ -n "$KEY" ]; then
  exec ./InnerslothGateway "$PORT" "$KEY"
else
  exec ./InnerslothGateway "$PORT"
fi
