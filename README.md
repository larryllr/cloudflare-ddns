# Cloudflare DDNS

一个 Windows 托盘版 Cloudflare DDNS 工具。作者：宽宽。

它会在程序启动时刷新一次 Cloudflare DNS；之后保持安静，只在你右键托盘图标点 `Refresh now` 时手动刷新。

## 功能

- 托盘常驻，无 PowerShell 弹窗
- 设置全图形化：邮箱、Global API Key / API Token、Zone、记录名、自定义 IP
- `Custom IP` 留空时自动检测公网 IPv4
- 公网 IPv4 检测禁用系统代理
- 支持开机自启
- API Key 使用 Windows 当前用户 DPAPI 加密保存
- 默认灰云 `DNS only`，适合 Sunshine/Moonlight 这类自定义端口直连场景

## 使用

运行：

```text
dist\CloudflareDDNS.exe
```

右键托盘图标，打开 `Settings`：

- `Zone`：Cloudflare 里的 zone，例如 `example.com`
- `Records`：要同步的 A 记录，例如 `home.example.com`
- `Email`：Cloudflare 登录邮箱，仅 Global API Key 模式需要
- `API Key / Token`：Global API Key 或 API Token
- `Custom IP`：留空自动 DDNS；填写 IPv4 时固定定向到这个 IP
- `Refresh when app starts`：程序启动时刷新一次
- `Start with Windows`：开机自启

保存后，可右键托盘图标点 `Refresh now` 立即刷新。

## Cloudflare 权限

推荐使用 API Token，权限设置为：

- `Zone / Zone / Read`
- `Zone / DNS / Edit`

如果使用 Global API Key，也可以在设置里勾选 `Use Global API Key`，但它是账号级密钥，风险更高。

## 代理说明

本工具是纯 DNS DDNS，不提供花生壳那类穿透或中转。Cloudflare 记录建议保持灰云 `DNS only`。

访问 Sunshine 这类服务时请使用：

```text
https://你的域名:端口
```

如果浏览器、VPN 或代理软件接管了这个域名，可能会出现 Cloudflare 522 或连接超时。把域名加入代理软件的直连规则通常就能解决。

## 构建

在 Windows PowerShell 里运行：

```powershell
.\build.ps1
```

构建产物位于：

```text
dist\CloudflareDDNS.exe
```
