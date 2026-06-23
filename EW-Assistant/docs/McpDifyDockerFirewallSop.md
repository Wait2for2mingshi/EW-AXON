# Dify Docker 访问 Windows MCP Server 防火墙排查 SOP

## 适用场景

- Windows 上运行 `McpServer`，对外提供 MCP SSE，例如 `http://192.168.200.10:5001/sse`。
- Mac 宿主机可以 `curl` 到 Windows MCP Server。
- Dify 运行在 Mac Docker 中，`plugin_daemon` 容器内访问同一地址失败，例如 `Connection refused`。
- 不希望重置 Dify 数据、不删除 `volumes/plugin_daemon`，也不希望一开始重置 Windows 防火墙。

## 排查原则

- 先观察，再修改。
- 不重置 Windows 防火墙。
- 不删除 Dify volume 或插件数据。
- Dify MCP SSE URL 始终填写 Windows 真实网卡 IP，例如 `http://192.168.200.10:5001/sse`，不要填写 `0.0.0.0`。
- `0.0.0.0` 只用于 Windows MCP Server 服务端监听，不用于客户端访问。

## 现场基线

示例地址如下，现场可按实际值替换：

- Windows IP：`192.168.200.10`
- Mac IP：`192.168.200.20`
- MCP 端口：`5001`
- Dify MCP SSE URL：`http://192.168.200.10:5001/sse`

## 1. 确认 Windows MCP Server 监听状态

在 Windows PowerShell 中执行：

```powershell
Get-NetTCPConnection -LocalPort 5001 -State Listen |
  Select-Object LocalAddress, LocalPort, State, OwningProcess |
  Format-Table -AutoSize
```

期望结果之一：

```text
LocalAddress LocalPort State  OwningProcess
------------ --------- -----  -------------
0.0.0.0           5001 Listen ...
```

或：

```text
LocalAddress    LocalPort State  OwningProcess
------------    --------- -----  -------------
192.168.200.10       5001 Listen ...
```

建议最终使用 `0.0.0.0:5001` 监听，方便 Docker Desktop/vpnkit/NAT 这类跨网络路径访问。客户端仍访问 `192.168.200.10:5001`。

## 2. 从 Mac 宿主机验证 MCP SSE

在 Mac 终端执行：

```bash
curl -N -v --max-time 10 http://192.168.200.10:5001/sse
```

成功时通常能看到：

```text
Connected to 192.168.200.10 port 5001
HTTP/1.1 200 OK
Content-Type: text/event-stream
event: endpoint
data: /message?sessionId=...
```

该步骤成功只能证明 Mac 宿主机到 Windows 通，不代表 Docker 容器到 Windows 一定通。

## 3. 从 Dify plugin_daemon 容器验证

在 Dify Docker 目录执行：

```bash
cd /Users/ew/Downloads/dify-main/docker
docker compose exec plugin_daemon sh -lc 'curl -N -v --max-time 10 http://192.168.200.10:5001/sse'
```

若出现：

```text
connect to 192.168.200.10 port 5001 from 172.18.0.x failed: Connection refused
```

说明 Dify 配置和 URL 大概率没有问题，问题集中在 Docker 容器出站到 Windows MCP 的 TCP 连接层。

## 4. 手工查看 Windows 防火墙规则

打开高级防火墙界面：

```text
Win + R -> wf.msc
```

进入：

```text
入站规则
```

重点检查：

- 是否存在针对 `TCP 5001` 的阻止规则。
- 是否存在只允许特定远程地址的规则。
- Profile 是否覆盖当前网络：`Domain`、`Private`、`Public`。
- Program 是否绑定旧路径的 `McpServer.exe`。
- 是否有比允许规则更明确的阻止规则。

## 5. 用 PowerShell 列出 TCP 5001 入站规则

在 Windows PowerShell 中执行：

```powershell
Get-NetFirewallRule -Direction Inbound |
  ForEach-Object {
    $rule = $_
    $port = $rule | Get-NetFirewallPortFilter
    $addr = $rule | Get-NetFirewallAddressFilter
    if ($port.Protocol -eq "TCP" -and $port.LocalPort -contains "5001") {
      [PSCustomObject]@{
        Name = $rule.DisplayName
        Enabled = $rule.Enabled
        Action = $rule.Action
        Profile = $rule.Profile
        Protocol = $port.Protocol
        LocalPort = $port.LocalPort
        RemoteAddress = $addr.RemoteAddress
      }
    }
  } | Format-Table -AutoSize
```

如果看到 `Action=Block` 且覆盖 `5001`，优先禁用或调整该规则，而不是重置防火墙。

## 6. 开启 Windows 防火墙日志

`pfirewall.log` 是 Windows Defender 防火墙自带的文本日志文件。默认情况下它可能没有记录拦截明细，所以“开 `pfirewall.log`”的意思不是打开一个现成文件，而是先开启防火墙日志记录开关，再复现一次问题，最后去日志文件里看有没有被拦截。

这一步不会重置防火墙规则，也不会修改 Dify 数据。它只是让 Windows 把允许或拦截的连接写到日志里。

先打开管理员 PowerShell：

```text
开始菜单 -> 搜索 PowerShell -> 右键 Windows PowerShell -> 以管理员身份运行
```

窗口标题通常会显示：

```text
管理员: Windows PowerShell
```

在管理员 PowerShell 中执行：

```powershell
Set-NetFirewallProfile -Profile Domain,Private,Public `
  -LogBlocked True `
  -LogAllowed True `
  -LogFileName "$env:SystemRoot\System32\LogFiles\Firewall\pfirewall.log"
```

参数含义：

- `-LogBlocked True`：记录被防火墙拦截的连接。
- `-LogAllowed True`：记录被允许的连接，日志会更详细，也会更长。
- `-LogFileName ...\pfirewall.log`：指定日志文件保存位置。

随后从 Mac 的 Dify Docker 目录重新执行容器内 curl，复现问题：

```bash
cd /Users/ew/Downloads/dify-main/docker
docker compose exec plugin_daemon sh -lc 'curl -N -v --max-time 10 http://192.168.200.10:5001/sse'
```

查看最近日志：

```powershell
Get-Content "$env:SystemRoot\System32\LogFiles\Firewall\pfirewall.log" -Tail 120
```

也可以直接搜索最近日志里是否有 `DROP`、`TCP`、`5001` 同时出现：

```powershell
Get-Content "$env:SystemRoot\System32\LogFiles\Firewall\pfirewall.log" -Tail 300 |
  Select-String -Pattern "DROP.*TCP.*5001"
```

判断方法：

- 如果看到类似 `DROP TCP ... 5001`，说明 Windows 防火墙确实拦截了容器访问 MCP 的连接。
- 如果只看到 `ALLOW TCP ... 5001`，说明 Windows Defender 防火墙本身大概率没有拦截这条连接。
- 如果完全没有 `5001` 相关记录，可能是连接没有到达 Windows 防火墙日志层，也可能是被更底层/第三方安全软件处理了。

排查完成后，如果觉得 `ALLOW` 日志太多，可以只关闭允许日志，保留拦截日志：

```powershell
Set-NetFirewallProfile -Profile Domain,Private,Public -LogAllowed False
```

## 7. 可选开启 WFP 审计

如果 `pfirewall.log` 看不出结果，可开启 Windows Filtering Platform 审计。WFP 是 Windows 更底层的网络过滤框架，Windows 防火墙和一些安全策略都会经过它。

这一步也不是重置防火墙，它只是让 Windows 安全日志记录更多网络允许/阻止事件。日志会比较多，建议只在复现问题时短时间开启。

在管理员 PowerShell 中执行：

```powershell
auditpol /set /subcategory:"Filtering Platform Packet Drop" /failure:enable
auditpol /set /subcategory:"Filtering Platform Connection" /failure:enable /success:enable
```

复现后打开：

```text
事件查看器 -> Windows 日志 -> 安全
```

筛选事件 ID：

```text
5157, 5152, 5156
```

含义：

- `5157`：连接被阻止。
- `5152`：数据包被阻止。
- `5156`：连接被允许。

查看方式：

1. 打开“事件查看器”。
2. 进入“Windows 日志”。
3. 点击“安全”。
4. 右侧点击“筛选当前日志”。
5. 在“事件 ID”里输入 `5157,5152,5156`。
6. 点击确定。

重点看事件内容里的字段：

- `Source Address` 或“源地址”：谁发起连接。
- `Destination Address` 或“目标地址”：是否为 Windows IP。
- `Destination Port` 或“目标端口”：是否为 `5001`。
- `Protocol`：是否为 TCP。
- `Application Name` 或“应用程序”：关联到哪个进程。

如果看到 `5157` 或 `5152`，目标端口是 `5001`，说明连接在 WFP/防火墙层面被阻止。此时优先新增或调整 TCP 5001 入站允许规则。

排查完成后，可关闭刚才打开的 WFP 审计，减少安全日志噪音：

```powershell
auditpol /set /subcategory:"Filtering Platform Packet Drop" /failure:disable
auditpol /set /subcategory:"Filtering Platform Connection" /failure:disable /success:disable
```

## 8. 没有 DROP 但仍 Connection refused

如果容器里仍然报：

```text
Connection refused
```

但 `pfirewall.log` 没有 `DROP TCP ... 5001`，按下面顺序继续看。

第一，看 MCP 是否监听在 `0.0.0.0:5001`。

在 Windows PowerShell 中执行：

```powershell
Get-NetTCPConnection -LocalPort 5001 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess |
  Format-Table -AutoSize
```

推荐结果：

```text
LocalAddress LocalPort OwningProcess
------------ --------- -------------
0.0.0.0           5001 ...
```

如果仍是：

```text
192.168.200.10    5001 ...
```

说明 MCP 仍只绑定在具体网卡 IP 上。建议确认 `McpServer` 已重新编译、部署并重启，让新监听逻辑生效。

第二，确认监听 5001 的进程是不是 MCP。

先拿到 `OwningProcess` 的 PID，然后执行：

```powershell
Get-Process -Id <PID> | Select-Object Id, ProcessName, Path
```

如果占用端口的不是 `McpServer.exe`，说明端口可能被其他程序占用，需要停止占用进程或换端口。

第三，看 Windows 侧是否能本机访问。

在 Windows PowerShell 中执行：

```powershell
curl.exe -N -v --max-time 10 http://127.0.0.1:5001/sse
curl.exe -N -v --max-time 10 http://192.168.200.10:5001/sse
```

如果 Windows 本机都失败，优先查 MCP 进程、端口和监听地址。如果 Windows 本机成功、Mac 宿主机成功、只有 Docker 容器失败，再继续看 Docker Desktop/vpnkit/NAT 或第三方安全软件。

第四，看是否有第三方安全软件或网络防护。

常见线索：

- Windows Defender 之外还有企业终端安全软件。
- 安全软件有“网络防护”“入侵防护”“应用联网控制”“防火墙增强”功能。
- Windows 防火墙日志没有 `DROP`，但连接仍被主动拒绝。

处理建议：

- 不要直接卸载安全软件。
- 优先临时查看它的网络拦截日志。
- 只针对 `McpServer.exe` 或 TCP `5001` 做白名单。
- 白名单后重新从 `plugin_daemon` 容器执行 curl 验证。

### 如何定位具体是哪一个第三方安全软件

如果怀疑不是 Windows Defender 防火墙，而是第三方安全软件、EDR、VPN 或网络防护组件影响，可以按下面顺序确认。

第一，列出 Windows 安全中心识别到的杀毒和防火墙产品。

在 Windows PowerShell 中执行：

```powershell
Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
  Select-Object displayName, pathToSignedProductExe, pathToSignedReportingExe, productState |
  Format-List

Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct -ErrorAction SilentlyContinue |
  Select-Object displayName, pathToSignedProductExe, pathToSignedReportingExe, productState |
  Format-List
```

如果这里出现了非 Microsoft 的产品名称，例如企业终端安全、EDR、防火墙增强、杀毒软件，它们就是优先排查对象。

第二，列出正在运行的安全、VPN、代理相关服务。

```powershell
Get-Service |
  Where-Object {
    $_.DisplayName -match "安全|防火墙|终端|杀毒|网络防护|EDR|Endpoint|Firewall|Security|Antivirus|VPN|Proxy|Clash|McAfee|Trellix|Symantec|Sophos|CrowdStrike|Sentinel|ESET|Kaspersky|Trend|Norton|360|火绒|奇安信|天擎"
  } |
  Sort-Object DisplayName |
  Select-Object Status, Name, DisplayName |
  Format-Table -AutoSize
```

这个命令是线索扫描，不是定论。看到可疑产品后，去对应软件界面里找“网络防护”“防火墙”“入侵防护”“应用联网控制”“拦截日志”。

第三，查看最近的 WFP 阻止事件。

先按第 7 节开启 WFP 审计，然后从 `plugin_daemon` 容器复现 curl。复现后在 Windows PowerShell 中执行：

```powershell
Get-WinEvent -FilterHashtable @{
  LogName = "Security"
  Id = 5157,5152
  StartTime = (Get-Date).AddMinutes(-15)
} |
  Select-Object -First 20 TimeCreated, Id, Message |
  Format-List
```

重点看事件内容里是否出现：

- `Destination Port: 5001`
- `Protocol: 6`，表示 TCP
- `Source Address`
- `Application Name`
- `Filter Run-Time ID`
- `Layer Name`

如果事件里显示目标端口是 `5001`，说明连接确实被 Windows Filtering Platform 层阻止。`Filter Run-Time ID` 有时可以帮助 IT 或安全软件厂商继续定位是哪条过滤器规则。

第四，导出 WFP 当前状态，搜索第三方 provider 名称。

```powershell
netsh wfp show state file="$env:TEMP\wfp-state.xml"
notepad "$env:TEMP\wfp-state.xml"
```

在打开的文件里搜索安全软件名称，例如：

```text
McAfee
Trellix
Symantec
Sophos
CrowdStrike
Sentinel
ESET
Kaspersky
Trend
360
火绒
奇安信
天擎
```

如果 WFP 状态里能看到某个第三方 provider，而 WFP 安全日志又出现了 `5157/5152`，基本就可以把范围收敛到这个产品或它的网络防护模块。

第五，做最小可逆验证。

在允许的维护窗口内，只临时关闭某个安全软件的“网络防护/防火墙/入侵防护”模块，不要卸载软件，也不要关闭所有安全能力。关闭后立刻从 `plugin_daemon` 容器复测：

```bash
docker compose exec plugin_daemon sh -lc 'curl -N -v --max-time 10 http://192.168.200.10:5001/sse'
```

判断：

- 关闭某个模块后容器访问立刻成功：该产品或该模块就是高概率影响源。
- 重新开启后又失败：基本坐实。
- 关闭后仍失败：继续看下一个候选产品，或回到监听地址、端口占用、Docker Desktop/vpnkit/NAT 方向。

最终处理建议是给 `McpServer.exe` 或 TCP `5001` 加白名单，不建议长期关闭安全软件。

## 9. 最小放行方案

确认需要放行时，优先新增一条明确的 TCP 入站允许规则，而不是重置防火墙。

仓库已提供工具：

```text
Tools\McpNetworkSetup\Configure-McpWindowsAccess.ps1
```

在 Windows 管理员 PowerShell 中执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Tools\McpNetworkSetup\Configure-McpWindowsAccess.ps1 -Port 5001
```

默认行为：

- 新增或更新规则：`EW Assistant MCP Server TCP 5001`
- 方向：入站
- 协议：TCP
- 本地端口：5001
- Profile：Any
- RemoteAddress：Any
- Action：Allow

排障通过后，可按实际来源地址再收紧 `RemoteAddress`。不要直接假设容器内的 `172.18.0.x` 就是 Windows 看到的真实来源，因为 Docker Desktop for Mac 会经过 vpnkit/NAT。

## 10. MCP Server 监听建议

`McpServer` 应监听所有 IPv4 网卡：

```text
http://0.0.0.0:5001
```

但 Dify 页面中的 MCP SSE URL 仍填写：

```text
http://192.168.200.10:5001/sse
```

复测顺序：

```bash
curl -N -v --max-time 10 http://192.168.200.10:5001/sse
```

```bash
docker compose exec plugin_daemon sh -lc 'curl -N -v --max-time 10 http://192.168.200.10:5001/sse'
```

容器内成功后，再到 Dify 页面保存 MCP Server 授权配置。

## 11. 判定表

| 现象 | 可能原因 | 下一步 |
| --- | --- | --- |
| Mac 宿主机 curl 成功，容器 curl 失败 | Docker Desktop/vpnkit/NAT 到 Windows 的链路与宿主机直连不同 | 查 Windows 防火墙日志和 WFP 审计 |
| 容器 curl 显示 `Connection refused` | 连接被主动拒绝，常见于防火墙、服务端监听或策略拦截 | 查监听地址、入站规则、`DROP TCP` |
| 防火墙日志出现 `DROP TCP ... 5001` | Windows 防火墙拦截 | 新增或调整 TCP 5001 入站 Allow 规则 |
| 没有 `DROP`，但仍 refused | 可能是服务未监听正确地址、端口被其他进程占用或 NAT 路径异常 | 确认 `0.0.0.0:5001` 监听，检查进程和端口 |
| Dify 日志显示正确连接 `192.168.200.10:5001/sse` | Dify URL 配置不是根因 | 不要重置 Dify 数据或插件 volume |

## 12. 回滚与收紧

如需移除临时放行规则：

```powershell
Remove-NetFirewallRule -DisplayName "EW Assistant MCP Server TCP 5001"
```

如需收紧远程地址，先通过日志确认 Windows 实际看到的来源，再更新规则：

```powershell
.\Tools\McpNetworkSetup\Configure-McpWindowsAccess.ps1 `
  -Port 5001 `
  -RemoteAddress 192.168.200.20
```

如果收紧后容器访问再次失败，说明 Windows 看到的来源不等同于 Mac 宿主机 IP，需要根据防火墙日志或 WFP 事件里的实际来源调整。
