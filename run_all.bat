@echo off
chcp 65001 > nul
echo ====================================================================
echo   Roketsan Hackathon Asama 2 - Taktik Komuta Merkezi Baslatici
echo ====================================================================
echo.
echo [1/3] Backend servisi baslatiliyor (ASP.NET Core - Port 5299)...
start "1 - Backend API (Port 5299)" cmd /k "cd /d %~dp0backend\src\Backend.Api && dotnet run --urls http://localhost:5299"

echo [2/3] Detection servisi baslatiliyor (YOLO best-4.pt - Port 8002)...
start "2 - Detection YOLO (Port 8002)" cmd /k "cd /d %~dp0detection && python server.py"

echo [3/3] LLM Agent servisi baslatiliyor (GLM-5.3-Flash - Port 8001)...
start "3 - LLM Agent (Port 8001)" cmd /k "cd /d %~dp0llm_agent && python server.py"

echo.
echo ====================================================================
echo Tum servisler baslatildi!
echo.
echo   - Taktik Komuta Arayuzu:          http://localhost:5299
echo   - Backend Swagger Dokumantasyonu: http://localhost:5299/swagger
echo   - Detection Health Endpoint:      http://localhost:8002/health
echo   - LLM Agent Health Endpoint:      http://localhost:8001/health
echo.
echo Tarayici aciliyor...
timeout /t 3 /nobreak > nul
start http://localhost:5299
echo ====================================================================
pause
