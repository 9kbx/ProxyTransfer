# ProxyTransfer.BrowserPy

一个标准 Playwright Python 项目，用于通过 ProxyTransfer API 启动后的本地 HTTP 代理手工验证浏览器访问行为。

## 特点

- 启动时输入本地代理地址
- 省略 scheme 时默认按 `http://` 处理
- 使用标准 Playwright 浏览器启动，不包含任何隐身或规避检测逻辑
- 默认使用持久化用户目录，便于保留 Cookie 和会话状态

## 环境要求

- Python 3.10+
- 已启动 ProxyTransfer.Api，并已导入/启动至少一个本地转发代理

## 安装

```bash
cd /Users/j/myprojects/Proxy/codes/ProxyTransfer/ProxyTransfer.BrowserPy
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python -m playwright install chromium
```

## 运行

```bash
cd /Users/j/myprojects/Proxy/codes/ProxyTransfer/ProxyTransfer.BrowserPy
source .venv/bin/activate
python main.py
```

启动后会提示输入：

- 本地代理地址，例如 `http://127.0.0.1:40000` 或 `127.0.0.1:40000`
- 目标网址，默认是 `https://api.ipify.org/`
- 是否使用无头模式

## 说明

这个项目用于合规的联调和行为观察，不尝试绕过站点验证、挑战页或自动化检测。
