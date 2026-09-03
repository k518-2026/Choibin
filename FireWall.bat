@echo off
chcp 65001 > nul
echo 「ちょい便」が使うポートを Windows ファイアウォールで許可します。
echo （管理者として実行してください）
echo.

netsh advfirewall firewall delete rule name="Choibin Discovery UDP 53317" > nul 2>&1
netsh advfirewall firewall delete rule name="Choibin Transfer TCP 53318" > nul 2>&1
netsh advfirewall firewall delete rule name="Choibin Phone TCP 53319" > nul 2>&1

netsh advfirewall firewall add rule name="Choibin Discovery UDP 53317" dir=in action=allow protocol=UDP localport=53317 profile=private,domain
netsh advfirewall firewall add rule name="Choibin Transfer TCP 53318" dir=in action=allow protocol=TCP localport=53318 profile=private,domain
netsh advfirewall firewall add rule name="Choibin Phone TCP 53319" dir=in action=allow protocol=TCP localport=53319 profile=private,domain

echo.
echo 完了しました。
pause
