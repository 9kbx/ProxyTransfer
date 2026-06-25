#!/usr/bin/env bash
# restart_tunnel.sh — 放在远端服务器上直接执行，重启 TunnelHost 容器。
# 用法:
#   ./restart_tunnel.sh
#   ./restart_tunnel.sh /path/to/tunnel.runtime.env
#
# 默认读取 /apps/proxytransfer/config/tunnel.runtime.env，
# Docker 构建上下文默认为本脚本所在目录。

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
RUNTIME_ENV_FILE="${1:-/apps/proxytransfer/config/tunnel.runtime.env}"

log() {
	printf '[restart-tunnel] %s\n' "$*"
}

fail() {
	printf '[restart-tunnel] %s\n' "$*" >&2
	exit 1
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
		[[ "$line" == *=* ]] || {
			printf '[restart-tunnel] 配置行无效: %s\n' "$line" >&2
			return 1
		}

		key="${line%%=*}"
		value="${line#*=}"
		key="$(trim_whitespace "$key")"
		value="$(trim_whitespace "$value")"

		[[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || {
			printf '[restart-tunnel] 配置键无效: %s\n' "$key" >&2
			return 1
		}

		if [[ ${#value} -ge 2 ]]; then
			quote_char="${value:0:1}"
			if [[ "$quote_char" == '"' || "$quote_char" == "'" ]]; then
				if [[ "${value: -1}" != "$quote_char" ]]; then
					printf '[restart-tunnel] 配置值引号不匹配: %s\n' "$key" >&2
					return 1
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

ensure_network_exists() {
	local docker_bin="$1"
	local network_name="$2"
	[[ -n "$network_name" ]] || return 0
	"$docker_bin" network inspect "$network_name" >/dev/null 2>&1 || "$docker_bin" network create "$network_name" >/dev/null
}

check_port_conflicts() {
	local container_name="$1"
	local port_specs="$2"
	local docker_bin="$3"
	local mapping protocol host_binding host_port_spec current_port start_port end_port details
	local -a conflicts
	local docker_filter_value

	[[ -n "$port_specs" ]] || return 0
	command -v ss >/dev/null 2>&1 || {
		log '本机缺少 ss，跳过端口占用预检。'
		return 0
	}

	conflicts=()
	IFS='|' read -r -a mappings <<< "$port_specs"
	for mapping in "${mappings[@]}"; do
		[[ -n "$mapping" ]] || continue
		protocol=tcp
		if [[ "$mapping" == */* ]]; then
			protocol="${mapping##*/}"
			mapping="${mapping%/*}"
		fi

		[[ "$mapping" == *:* ]] || continue
		host_binding="${mapping%:*}"
		host_port_spec="${host_binding##*:}"
		[[ -n "$host_port_spec" ]] || continue

		if [[ "$host_port_spec" == *-* ]]; then
			start_port="${host_port_spec%-*}"
			end_port="${host_port_spec#*-}"
		else
			start_port="$host_port_spec"
			end_port="$host_port_spec"
		fi

		for ((current_port = start_port; current_port <= end_port; current_port++)); do
			docker_filter_value="$current_port"
			if "$docker_bin" ps --filter "name=^/$container_name$" --filter "publish=$docker_filter_value" --format '{{.ID}}' | grep -q .; then
				continue
			fi

			case "$protocol" in
				udp)
					details="$(ss -Hplnu "sport = :$current_port" 2>/dev/null || true)"
					;;
				*)
					details="$(ss -Hplnt "sport = :$current_port" 2>/dev/null || true)"
					;;
			esac

			if [[ -n "$details" ]]; then
				conflicts+=("$current_port/$protocol => $details")
			fi
		done
	done

	if [[ "${#conflicts[@]}" -gt 0 ]]; then
		log '端口冲突，容器未启动。请调整 CONTAINER_PORTS 或释放这些端口：'
		printf '  - %s\n' "${conflicts[@]}"
		return 1
	fi
}

# ---------- 主流程 ----------

# 可在此脚本同级目录的 .env 文件中覆盖容器标识默认值：
#   TUNNEL_REMOTE_IMAGE_NAME / REMOTE_IMAGE_NAME
#   TUNNEL_REMOTE_CONTAINER_NAME / REMOTE_CONTAINER_NAME
#   REMOTE_DOCKERFILE_NAME / REMOTE_DOCKER_CONTEXT
LOCAL_ENV_FILE="$SCRIPT_DIR/.env"
if [[ -f "$LOCAL_ENV_FILE" ]]; then
	load_env_file "$LOCAL_ENV_FILE"
fi

IMAGE_NAME="${TUNNEL_REMOTE_IMAGE_NAME:-${REMOTE_IMAGE_NAME:-proxytransfer-tunnel:latest}}"
CONTAINER_NAME="${TUNNEL_REMOTE_CONTAINER_NAME:-${REMOTE_CONTAINER_NAME:-proxytransfer-tunnel}}"
DOCKERFILE_NAME="${REMOTE_DOCKERFILE_NAME:-Dockerfile}"
DOCKER_CONTEXT="${REMOTE_DOCKER_CONTEXT:-$SCRIPT_DIR}"

log "镜像: $IMAGE_NAME"
log "容器: $CONTAINER_NAME"
log "Dockerfile: $DOCKERFILE_NAME"
log "构建上下文: $DOCKER_CONTEXT"

# 加载运行时配置（端口、挂载、网络等）
if [[ -f "$RUNTIME_ENV_FILE" ]]; then
	log "加载运行时配置: $RUNTIME_ENV_FILE"
	load_env_file "$RUNTIME_ENV_FILE"
else
	log "未找到运行时配置文件: $RUNTIME_ENV_FILE（使用默认值）"
fi

DOCKER_BIN="${DOCKER_BIN:-docker}"
RESTART_POLICY="${CONTAINER_RESTART:-unless-stopped}"
PORTS="${CONTAINER_PORTS:-}"
VOLUMES="${CONTAINER_VOLUMES:-}"
ENVS="${CONTAINER_ENVS:-}"
NETWORK="${CONTAINER_NETWORK:-}"
EXTRA_ARGS="${CONTAINER_EXTRA_ARGS:-}"

ensure_network_exists "$DOCKER_BIN" "$NETWORK"

docker_run_args=(run -d --name "$CONTAINER_NAME" --restart "$RESTART_POLICY")

if [[ -n "$PORTS" ]]; then
	split_delimited_values "$PORTS"
	for item in "${SPLIT_VALUES[@]}"; do
		docker_run_args+=(-p "$item")
	done
fi

if [[ -n "$VOLUMES" ]]; then
	split_delimited_values "$VOLUMES"
	for item in "${SPLIT_VALUES[@]}"; do
		docker_run_args+=(-v "$item")
	done
fi

if [[ -n "$ENVS" ]]; then
	split_delimited_values "$ENVS"
	for item in "${SPLIT_VALUES[@]}"; do
		docker_run_args+=(-e "$item")
	done
fi

if [[ -n "$NETWORK" ]]; then
	docker_run_args+=(--network "$NETWORK")
fi

if [[ -n "$EXTRA_ARGS" ]]; then
	split_delimited_values "$EXTRA_ARGS"
	for item in "${SPLIT_VALUES[@]}"; do
		docker_run_args+=("$item")
	done
fi

docker_run_args+=("$IMAGE_NAME")

log "构建镜像..."
cd "$DOCKER_CONTEXT"
"$DOCKER_BIN" build -t "$IMAGE_NAME" -f "$DOCKERFILE_NAME" .

log "检查端口冲突..."
check_port_conflicts "$CONTAINER_NAME" "$PORTS" "$DOCKER_BIN"

log "停止旧容器..."
"$DOCKER_BIN" rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

log "启动新容器..."
"$DOCKER_BIN" "${docker_run_args[@]}"

log "重启完成: $CONTAINER_NAME"
