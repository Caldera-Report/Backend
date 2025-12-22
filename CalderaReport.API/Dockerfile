FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
ARG BUILD_CONFIGURATION=Release
COPY ["CalderaReport.API/CalderaReport.API.csproj", "CalderaReport.API/"]
COPY ["CalderaReport.Services/CalderaReport.Services.csproj", "CalderaReport.Services/"]
COPY ["CalderaReport.Clients/CalderaReport.Clients.csproj", "CalderaReport.Clients/"]
COPY ["CalderaReport.Domain/CalderaReport.Domain.csproj", "CalderaReport.Domain/"]
RUN dotnet restore "CalderaReport.API/CalderaReport.API.csproj"

FROM restore AS publish
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR /src/CalderaReport.API
RUN dotnet publish "CalderaReport.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Listen on 8080 by default; override ASPNETCORE_URLS at runtime if needed.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    Cors__AllowedOrigins=
RUN useradd --create-home --uid 1001 --shell /usr/sbin/nologin apiuser
COPY --from=publish /app/publish .
EXPOSE 8080
USER apiuser
ENTRYPOINT ["dotnet", "CalderaReport.API.dll"]
