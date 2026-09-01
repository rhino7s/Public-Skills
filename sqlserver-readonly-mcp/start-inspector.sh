#!/bin/sh
set -eu

project_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
inspector_config_path="$project_directory/inspector.config.unix.local.json"
sql_config_path="$project_directory/appsettings.local.json"

if ! command -v mcp-inspector >/dev/null 2>&1; then
    printf '%s\n' '找不到 mcp-inspector。请先安装：npm install -g @modelcontextprotocol/inspector@latest' >&2
    exit 1
fi

if [ ! -f "$inspector_config_path" ]; then
    printf 'Inspector 配置文件不存在：%s\n' "$inspector_config_path" >&2
    exit 1
fi

if [ ! -f "$sql_config_path" ]; then
    printf 'SQL MCP 配置文件不存在：%s\n' "$sql_config_path" >&2
    exit 1
fi

cd "$project_directory"
exec mcp-inspector --web --config "$inspector_config_path"
