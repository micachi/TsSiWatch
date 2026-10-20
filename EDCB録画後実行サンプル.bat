@echo off
rem ============================================================
rem  EDCB 録画後実行BAT サンプル
rem  EDCB の設定 → 動作設定 → 録画後実行BAT にこのファイルを指定します。
rem  (EDCB は録画ファイルパスを第 1 引数に渡します)
rem  TsSiWatch.exe はこの bat と同じフォルダに置いてください。
rem ============================================================
"%~dp0TsSiWatch.exe" "%~1"
set RC=%errorlevel%
if not "%RC%"=="0" echo [TsSiWatch] エラーが発生しました (exit %RC%)
exit /b %RC%
