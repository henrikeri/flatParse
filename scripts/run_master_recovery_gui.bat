@echo offvvaa


setlocal
cd /d "%~dp0"
where py >nul 2>nul
if %errorlevel% equ 0 (
    py -3 master_recovery_gui.py
) else (
    python master_recovery_gui.py
)
if errorlevel 1 pause
endlocal
