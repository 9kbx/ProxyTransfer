#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
ENV_FILE="${1:-$SCRIPT_DIR/.env}"

log() {
	printf '[publish-tunnel] %s\n' "$*"
}

fail() {
	printf '[publish-tunnel] %s\n' "$*" >&2
	exit 1
}

require_command() {
	command -v "$1" >/dev/null 2>&1 || fail "缺少命令: $1"
}

require_env() {
	local name="$1"
	[[ -n "${!name:-}" ]] || fail "缺少配置: $name"
}

trim_whitespace() {
	local value="$1"
	value="${value#"${value%%[![:space:]]*}"}"
	value="${value%"${value##*[![:space:]]}"}"
	printf '%s' "$value"
}

load_env_file() {
	local env_path="$1"
	local line key value quote_char

	while IFS= read -r line || [[ -n "$line" ]]; do
		line="${line%$'\r'}"
		[[ -n "$line" ]] || continue
		[[ "$line" =~ ^[[:space:]]*# ]] && continue
		[[ "$line" == *=* ]] || fail "无效配置行: $line"

		key="${line%%=*}"
		value="${line#*=}"
		key="$(trim_whitespace "$key")"
		value="$(trim_whitespace "$value")"

		[[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || fail "无效配置键: $key"

		if [[ ${#value} -ge 2 ]]; then
			quote_char="${value:0:1}"
			if [[ "$quote_char" == '"' || "$quote_char" == "'" ]]; then
				if [[ "${value: -1}" != "$quote_char" ]]; then
					fail "配置值引号不匹配: $key"
				fi
				value="${value:1:${#value}-2}"
			fi
		fi

		export "$key=$value"
	done < "$env_path"
}

split_delimited_values() {
	local value="$1"
	local item
	SPLIT_VALUES=()
	IFS='|' read -r -a parts <<< "$value"
	for item in "${parts[@]}"; do
		[[ -n "$item" ]] || continue
		SPLIT_VALUES+=("$item")
	done
}

[[ -f "$ENV_FILE" ]] || fail "未找到配置文件: $ENV_FILE"

load_env_file "$ENV_FILE"

require_command dotnet
require_command rsync
require_command ssh

TUNNEL_PROJECT_PATH="${TUNNEL_PROJECT_PATH:-$ROOT_DIR/ProxyTransfer.TunnelHost/ProxyTransfer.TunnelHost.csproj}"
TUNNEL_PROJECT_DIR="$(cd -- "$(dirname -- "$TUNNEL_PROJECT_PATH")" && pwd)"
TUNNEL_DOCKERFILE_PATH="${TUNNEL_DOCKERFILE_PATH:-$TUNNEL_PROJECT_DIR/Dockerfile}"
PUBLISH_CONFIGURATION="${PUBLISH_CONFIGURATION:-Release}"
PUBLISH_DIR="${PUBLISH_DIR:-$ROOT_DIR/.artifacts/publish/tunnel}"
REMOTE_PORT="${REMOTE_PORT:-22}"
REMOTE_DOCKERFILE_NAME="${REMOTE_DOCKERFILE_NAME:-Dockerfile}"
REMOTE_IMAGE_NAME="${TUNNEL_REMOTE_IMAGE_NAME:-proxytransfer-tunnel:latest}"
REMOTE_CONTAINER_NAME="${TUNNEL_REMOTE_CONTAINER_NAME:-proxytransfer-tunnel}"
REMOTE_PATH="${TUNNEL_REMOTE_PATH:-/apps/proxytransfer/tunnel}"
REMOTE_RUNTIME_ENV_PATH="${TUNNEL_REMOTE_RUNTIME_ENV_PATH:-/apps/proxytransfer/config/tunnel.runtime.env}"
CONTAINER_RESTART="${CONTAINER_RESTART:-unless-stopped}"
DOCKER_BIN="${DOCKER_BIN:-docker}"
RSYNC_DELETE="${RSYNC_DELETE:-true}"

require_env REMOTE_HOST
require_env REMOTE_USER
require_env REMOTE_PATH

REMOTE_DOCKER_CONTEXT="${REMOTE_DOCKER_CONTEXT:-$REMOTE_PATH}"

[[ -f "$TUNNEL_DOCKERFILE_PATH" ]] || fail "未找到 Dockerfile: $TUNNEL_DOCKERFILE_PATH"

remote_target="$REMOTE_USER@$REMOTE_HOST"
ssh_args=(-p "$REMOTE_PORT")
rsync_args=(-az)

if [[ -n "${SSH_EXTRA_ARGS:-}" ]]; then
	IFS='|' read -r -a ssh_extra_parts <<< "$SSH_EXTRA_ARGS"
	ssh_args+=("${ssh_extra_parts[@]}")
	rsync_args+=("-e" "ssh $(printf '%q ' "${ssh_args[@]}")")
else
	rsync_args+=("-e" "ssh -p $REMOTE_PORT")
fi

if [[ "$RSYNC_DELETE" == "true" ]]; then
	rsync_args+=(--delete)
fi

log "发布 TunnelHost"
rm -rf "$PUBLISH_DIR"
dotnet publish "$TUNNEL_PROJECT_PATH" -c "$PUBLISH_CONFIGURATION" -o "$PUBLISH_DIR"
cp "$TUNNEL_DOCKERFILE_PATH" "$PUBLISH_DIR/$REMOTE_DOCKERFILE_NAME"

# 复制独立重启脚本到发布目录，方便运维在服务器上直接执行
cp "$SCRIPT_DIR/restart_tunnel.sh" "$PUBLISH_DIR/"

log "同步发布目录到远端"
ssh "${ssh_args[@]}" "$remote_target" "mkdir -p $(printf '%q' "$REMOTE_PATH")"
rsync "${rsync_args[@]}" "$PUBLISH_DIR/" "$remote_target:$REMOTE_PATH/"
remote_container_name_literal="$(printf '%q' "$REMOTE_CONTAINER_NAME")"
remote_image_name_literal="$(printf '%q' "$REMOTE_IMAGE_NAME")"
remote_dockerfile_name_literal="$(printf '%q' "$REMOTE_DOCKERFILE_NAME")"
remote_docker_context_literal="$(printf '%q' "$REMOTE_DOCKER_CONTEXT")"
remote_runtime_env_path_literal="$(printf '%q' "$REMOTE_RUNTIME_ENV_PATH")"
remote_container_restart_literal="$(printf '%q' "$CONTAINER_RESTART")"
remote_docker_bin_literal="$(printf '%q' "$DOCKER_BIN")"
remote_container_ports_literal="$(printf '%q' "${CONTAINER_PORTS:-}")"
remote_container_volumes_literal="$(printf '%q' "${CONTAINER_VOLUMES:-}")"
remote_container_envs_literal="$(printf '%q' "${CONTAINER_ENVS:-}")"
remote_container_network_literal="$(printf '%q' "${CONTAINER_NETWORK:-}")"
remote_container_extra_args_literal="$(printf '%q' "${CONTAINER_EXTRA_ARGS:-}")"

log "远端构建镜像并启动容器"
ssh "${ssh_args[@]}" "$remote_target" /bin/bash <<EOF
set -Eeuo pipefail

trim_whitespace() {
	local value="\$1"
	value="\${value#"\${value%%[![:space:]]*}"}"
	value="\${value%"\${value##*[![:space:]]}"}"
	printf '%s' "\$value"
}

load_env_file() {
	local env_path="\$1"
	local line key value quote_char

	while IFS= read -r line || [[ -n "\$line" ]]; do
		line="\${line%\$'\\r'}"
		[[ -n "\$line" ]] || continue
		[[ "\$line" =~ ^[[:space:]]*# ]] && continue
		[[ "\$line" == *=* ]] || {
			printf '[publish-tunnel] 远端运行时配置无效: %s\n' "\$line" >&2
			return 1
		}

		key="\${line%%=*}"
		value="\${line#*=}"
		key="\$(trim_whitespace "\$key")"
		value="\$(trim_whitespace "\$value")"

		[[ "\$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || {
			printf '[publish-tunnel] 远端运行时配置键无效: %s\n' "\$key" >&2
			return 1
		}

		if [[ \${#value} -ge 2 ]]; then
			quote_char="\${value:0:1}"
			if [[ "\$quote_char" == '"' || "\$quote_char" == "'" ]]; then
				if [[ "\${value: -1}" != "\$quote_char" ]]; then
					printf '[publish-tunnel] 远端运行时配置引号不匹配: %s\n' "\$key" >&2
					return 1
				fi
				value="\${value:1:\${#value}-2}"
			fi
		fi

		export "\$key=\$value"
	done < "\$env_path"
}

split_delimited_values() {
	local value="\$1"
	local item
	SPLIT_VALUES=()
	IFS='|' read -r -a parts <<< "\$value"
	for item in "\${parts[@]}"; do
		[[ -n "\$item" ]] || continue
		SPLIT_VALUES+=("\$item")
	done
}

REMOTE_CONTAINER_NAME_VALUE=$remote_container_name_literal
REMOTE_IMAGE_NAME_VALUE=$remote_image_name_literal
REMOTE_DOCKERFILE_NAME_VALUE=$remote_dockerfile_name_literal
REMOTE_DOCKER_CONTEXT_VALUE=$remote_docker_context_literal
REMOTE_RUNTIME_ENV_PATH_VALUE=$remote_runtime_env_path_literal
CONTAINER_RESTART=$remote_container_restart_literal
DOCKER_BIN=$remote_docker_bin_literal
CONTAINER_PORTS=$remote_container_ports_literal
CONTAINER_VOLUMES=$remote_container_volumes_literal
CONTAINER_ENVS=$remote_container_envs_literal
CONTAINER_NETWORK=$remote_container_network_literal
CONTAINER_EXTRA_ARGS=$remote_container_extra_args_literal

check_remote_port_conflicts() {
	local container_name="\$1"
	local port_specs="\$2"
	local docker_bin="\$3"
	local mapping protocol host_binding host_port_spec current_port start_port end_port details
	local -a conflicts
	local docker_filter_value

	[[ -n "\$port_specs" ]] || return 0
	command -v ss >/dev/null 2>&1 || {
		printf '[publish-tunnel] 远端缺少 ss，跳过端口占用预检。\n' >&2
		return 0
	}

	conflicts=()
	IFS='|' read -r -a mappings <<< "\$port_specs"
	for mapping in "\${mappings[@]}"; do
		[[ -n "\$mapping" ]] || continue
		protocol=tcp
		if [[ "\$mapping" == */* ]]; then
			protocol="\${mapping##*/}"
			mapping="\${mapping%/*}"
		fi

		[[ "\$mapping" == *:* ]] || continue
		host_binding="\${mapping%:*}"
		host_port_spec="\${host_binding##*:}"
		[[ -n "\$host_port_spec" ]] || continue

		if [[ "\$host_port_spec" == *-* ]]; then
			start_port="\${host_port_spec%-*}"
			end_port="\${host_port_spec#*-}"
		else
			start_port="\$host_port_spec"
			end_port="\$host_port_spec"
		fi

		for ((current_port = start_port; current_port <= end_port; current_port++)); do
			docker_filter_value="\$current_port"
			if "\$docker_bin" ps --filter "name=^/\$container_name$" --filter "publish=\$docker_filter_value" --format '{{.ID}}' | grep -q .; then
				continue
			fi

			case "\$protocol" in
				udp)
					details="\$(ss -Hplnu "sport = :\$current_port" 2>/dev/null || true)"
					;;
				*)
					details="\$(ss -Hplnt "sport = :\$current_port" 2>/dev/null || true)"
					;;
			esac

			if [[ -n "\$details" ]]; then
				conflicts+=("\$current_port/\$protocol => \$details")
			fi
		done
	done

	if [[ "\${#conflicts[@]}" -gt 0 ]]; then
		printf '[publish-tunnel] 远端端口冲突，容器未启动。请调整 CONTAINER_PORTS 或释放这些端口：\n' >&2
		printf '  - %s\n' "\${conflicts[@]}" >&2
		return 1
	fi
}

if [[ -n "\$REMOTE_RUNTIME_ENV_PATH_VALUE" ]]; then
	[[ -f "\$REMOTE_RUNTIME_ENV_PATH_VALUE" ]] || {
		printf '[publish-tunnel] 未找到远端运行时配置文件: %s\n' "\$REMOTE_RUNTIME_ENV_PATH_VALUE" >&2
		exit 1
	}
	load_env_file "\$REMOTE_RUNTIME_ENV_PATH_VALUE"
fi

DOCKER_BIN="\${DOCKER_BIN:-docker}"
CONTAINER_RESTART="\${CONTAINER_RESTART:-unless-stopped}"

docker_run_args=(run -d --name "\$REMOTE_CONTAINER_NAME_VALUE" --restart "\$CONTAINER_RESTART")

# 确保 Docker 网络存在
if [[ -n "\${CONTAINER_NETWORK:-}" ]]; then
	"\$DOCKER_BIN" network inspect "\$CONTAINER_NETWORK" >/dev/null 2>&1 || "\$DOCKER_BIN" network create "\$CONTAINER_NETWORK" >/dev/null
	docker_run_args+=(--network "\$CONTAINER_NETWORK")
fi

if [[ -n "\${CONTAINER_PORTS:-}" ]]; then
	split_delimited_values "\$CONTAINER_PORTS"
	for item in "\${SPLIT_VALUES[@]}"; do
		docker_run_args+=(-p "\$item")
	done
fi

if [[ -n "\${CONTAINER_VOLUMES:-}" ]]; then
	split_delimited_values "\$CONTAINER_VOLUMES"
	for item in "\${SPLIT_VALUES[@]}"; do
		docker_run_args+=(-v "\$item")
	done
fi

if [[ -n "\${CONTAINER_ENVS:-}" ]]; then
	split_delimited_values "\$CONTAINER_ENVS"
	for item in "\${SPLIT_VALUES[@]}"; do
		docker_run_args+=(-e "\$item")
	done
fi

if [[ -n "\${CONTAINER_EXTRA_ARGS:-}" ]]; then
	split_delimited_values "\${CONTAINER_EXTRA_ARGS}"
	for item in "\${SPLIT_VALUES[@]}"; do
		docker_run_args+=("\$item")
	done
fi

docker_run_args+=("\$REMOTE_IMAGE_NAME_VALUE")

cd \$REMOTE_DOCKER_CONTEXT_VALUE
"\$DOCKER_BIN" build -t "\$REMOTE_IMAGE_NAME_VALUE" -f "\$REMOTE_DOCKERFILE_NAME_VALUE" .
check_remote_port_conflicts "\$REMOTE_CONTAINER_NAME_VALUE" "\${CONTAINER_PORTS:-}" "\$DOCKER_BIN"
"\$DOCKER_BIN" rm -f "\$REMOTE_CONTAINER_NAME_VALUE" >/dev/null 2>&1 || true
"\$DOCKER_BIN" "\${docker_run_args[@]}"
${REMOTE_POST_DEPLOY:-:}
EOF

log "发布完成"
