dotnet build-server shutdown
dotnet nuget locals all --clear
for /d /r %%d in (bin obj) do @if exist "%%d" rd /s /q "%%d"
echo Nuked. Run 'dotnet restore' to rebuild.
