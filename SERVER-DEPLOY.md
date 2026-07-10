# TLAH Studio — 更新服务器部署指南

## 托管约定

更新服务使用 Nginx 暴露 `https://download.matrixlabs.cn/tlah/windows/`，物理目录为 `/var/www/download/tlah/windows/`。目录包含版本化安装包，以及固定名称的 `latest.json` 和 `latest.json.sig`。

```nginx
location /tlah/windows/ {
    alias /var/www/download/tlah/windows/;
    add_header Cache-Control "no-cache, must-revalidate";
    add_header Access-Control-Allow-Origin "*";
}
```

配置后运行：

```bash
sudo mkdir -p /var/www/download/tlah/windows
sudo chown -R "$USER":"$USER" /var/www/download/tlah/windows
sudo nginx -t
sudo systemctl reload nginx
```

只开放 HTTPS。ECDSA 更新私钥和 Authenticode 私钥均留在发布机，绝不上传。

## 标准发布顺序

1. 在 Windows 发布机运行 `tools/build-release.ps1`，完成版本同步、漏洞审计、测试、publish、签名、Inno Setup 和烟测。
2. 审查 `latest.json` 的版本、URL、SHA-256 和大小；提交代码与元数据，创建 `vX.Y.Z` 标签并推送 Git。
3. 运行 `tools/deploy.ps1 -Server user@download.matrixlabs.cn`。该脚本只验证并上传现有产物，不会重写已提交的清单。
4. 脚本先上传 `.uploading` 临时文件，再在服务器快速提升；`latest.json` 最后替换，减少客户端看到半发布状态的窗口。

## 线上验证

```bash
curl -fsS https://download.matrixlabs.cn/tlah/windows/latest.json
curl -fsSI https://download.matrixlabs.cn/tlah/windows/latest.json.sig
curl -fsSI https://download.matrixlabs.cn/tlah/windows/TLAHStudioSetup-X.Y.Z.exe
find /var/www/download/tlah/windows -maxdepth 1 -name '*.uploading' -print
```

本地还应再次运行：

```powershell
.\tools\verify-release.ps1 -Version X.Y.Z -AllowUntrustedAuthenticode -SkipSmokeInstall
```

客户端先验证 `latest.json` 的 ECDSA P-256 签名，再比较版本与稳定灰度分桶；下载后校验安装包 SHA-256，最后由 `TLAHStudio.Updater.exe` 执行静默安装。
