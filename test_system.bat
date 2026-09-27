@echo off
chcp 65001 > nul
echo ====================================================================
echo   Roketsan Hackathon Asama 2 - Sistem Test Kontrolu
echo ====================================================================
echo.
echo [1/3] Backend (Port 5299) kontrol ediliyor...
curl.exe -s http://localhost:5299/swagger/v1/swagger.json > nul
if %errorlevel% equ 0 (
    echo   [BASARILI] Backend calisiyor.
) else (
    echo   [UYARI] Backend henuz yanit vermiyor.
)

echo.
echo [2/3] Detection Servisi (Port 8002) kontrol ediliyor...
curl.exe -s http://localhost:8002/health > nul
if %errorlevel% equ 0 (
    echo   [BASARILI] Detection servisi hazir.
) else (
    echo   [UYARI] Detection servisi henuz yanit vermiyor.
)

echo.
echo [3/3] LLM Agent (Port 8001) kontrol ediliyor...
curl.exe -s http://localhost:8001/health > nul
if %errorlevel% equ 0 (
    echo   [BASARILI] LLM Agent servisi hazir.
) else (
    echo   [UYARI] LLM Agent servisi henuz yanit vermiyor.
)

echo.
echo ====================================================================
echo Uctan Uca Agent ve Karar Pipeline Testi Baslatiliyor (T0001)...
echo ====================================================================
cd /d %~dp0llm_agent
python test_pipeline.py T0001
echo.
echo ====================================================================
echo Test tamamlandi.
echo ====================================================================
