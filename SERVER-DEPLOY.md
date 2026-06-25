# TLAH Studio — 服务器端部署指南

## 服务器信息

| 项目 | 值 |
|------|-----|
| 域名 | **download.matrixlabs.cn** |
| 服务器 | **Nginx** |
| 文件路径 | `/tlah/windows/` |

## Nginx 配置

在你的 Nginx 配置中添加以下 server/location 块：

```nginx
# /etc/nginx/sites-available/tlah-download 或直接加入现有 server 块

location /tlah/windows/ {
    alias /var/www/download/tlah/windows/;
    autoindex on;                    # 可选：列出目录内容便于管理
    add_header Cache-Control "no-cache, must-revalidate";

    # CORS（如果 App 需要跨域检查）
    add_header Access-Control-Allow-Origin "*";

    # 强制 HTTPS
    if ($scheme != "https") {
        return 301 https://$host$request_uri;
    }
}

# 302 重定向：latest → 最新版本安装包（可选）
location /tlah/windows/latest {
    return 302 /tlah/windows/TLAHStudioSetup-1.0.0.exe;
}
```

重新加载 Nginx：
```bash
sudo nginx -t && sudo systemctl reload nginx
```

## 服务器目录结构

```bash
# SSH 到服务器后创建：
sudo mkdir -p /var/www/download/tlah/windows
sudo chown -R $USER:$USER /var/www/download

# 上传文件后，目录应该是：
/var/www/download/tlah/windows/
  ├── latest.json                          # 版本元数据
  ├── TLAHStudioSetup-1.0.0.exe            # 安装包
  └── ...
```

## 需要上传到服务器的文件

每次发布新版本时，上传两个文件到服务器：

```bash
# 本地执行：
scp TLAHStudio.Installer/latest.json user@download.matrixlabs.cn:/var/www/download/tlah/windows/
scp TLAHStudioSetup-1.0.0.exe user@download.matrixlabs.cn:/var/www/download/tlah/windows/
```

## 最新版 latest.json 已配置为

```json
{
  "version": "1.0.0",
  "channel": "stable",
  "platform": "windows",
  "arch": "x64",
  "installerUrl": "https://download.matrixlabs.cn/tlah/windows/TLAHStudioSetup-1.0.0.exe",
  "sha256": "REPLACE_WITH_ACTUAL_SHA256_OF_INSTALLER",
  "signatureUrl": "https://download.matrixlabs.cn/tlah/windows/TLAHStudioSetup-1.0.0.exe.sig",
  "releaseNotes": "Initial release of TLAH Studio.",
  "forceUpdate": false,
  "minSupportedVersion": "1.0.0"
}
```

## 获取真实 SHA256

```powershell
# 打包后，在本地执行：
Get-FileHash -Path "TLAHStudio.Installer\output\TLAHStudioSetup-1.0.0.exe" -Algorithm SHA256
# 将输出的 Hash 值填入 latest.json 的 sha256 字段（小写）
```

## 版本发布流程

1. `dotnet publish` → 编译生成 `TLAHStudio.App.exe`
2. 用 Inno Setup 编译 `setup.iss` → 生成 `TLAHStudioSetup-x.y.z.exe`
3. 计算 SHA256 → 更新 `latest.json`
4. 上传 `latest.json` + 安装包到 `/var/www/download/tlah/windows/`
5. 客户端启动 3 秒后自动检查 `latest.json` → 弹窗提醒更新
