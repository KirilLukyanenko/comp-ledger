FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props ./
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/CompLedger.Api/CompLedger.Api.csproj -c Release -o /app/publish -p:UseAppHost=false

# The full aspnet image on purpose: it has a shell and cat, which the load gate uses to read /proc/1/status.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
# Exec form, no shell wrapper: the API process must be PID 1.
ENTRYPOINT ["dotnet", "CompLedger.Api.dll"]
