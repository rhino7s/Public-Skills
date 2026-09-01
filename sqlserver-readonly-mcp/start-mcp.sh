#!/bin/sh
set -eu

project_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

case "$(uname -s):$(uname -m)" in
    Linux:x86_64|Linux:amd64)
        runtime_identifier='linux-x64'
        ;;
    Darwin:x86_64)
        runtime_identifier='osx-x64'
        ;;
    Darwin:arm64|Darwin:aarch64)
        runtime_identifier='osx-arm64'
        ;;
    *)
        printf '不支持当前平台或 CPU 架构：%s:%s\n' "$(uname -s)" "$(uname -m)" >&2
        exit 1
        ;;
esac

server_path="$project_directory/publish/$runtime_identifier/sqlserver-readonly-mcp"
config_path="${SQLSERVER_MCP_CONFIG:-$project_directory/appsettings.local.json}"

if [ "${1:-}" = '--config' ]; then
    if [ "$#" -lt 2 ]; then
        printf '%s\n' '--config 缺少配置文件路径。' >&2
        exit 2
    fi

    config_path=$2
    shift 2
fi

if [ ! -f "$server_path" ]; then
    printf '找不到 %s 发布文件：%s。请先运行 ./publish-all.sh。\n' "$runtime_identifier" "$server_path" >&2
    exit 1
fi

if [ ! -x "$server_path" ]; then
    printf '发布文件不可执行：%s。请运行 chmod 700。\n' "$server_path" >&2
    exit 1
fi

if [ ! -f "$config_path" ]; then
    printf 'SQL MCP 配置文件不存在：%s\n' "$config_path" >&2
    exit 2
fi

exec "$server_path" --config "$config_path" "$@"
