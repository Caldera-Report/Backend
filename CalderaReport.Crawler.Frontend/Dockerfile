FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
ARG BUILD_CONFIGURATION=Release
COPY ["CalderaReport.Crawler.Frontend/CalderaReport.Crawler.Frontend.csproj", "CalderaReport.Crawler.Frontend/"]
COPY ["CalderaReport.Domain/CalderaReport.Domain.csproj", "CalderaReport.Domain/"]
RUN dotnet restore "CalderaReport.Crawler.Frontend/CalderaReport.Crawler.Frontend.csproj"

FROM restore AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR /src/CalderaReport.Crawler.Frontend
RUN dotnet build "CalderaReport.Crawler.Frontend.csproj" -c $BUILD_CONFIGURATION --no-restore

FROM build AS publish
RUN dotnet publish "CalderaReport.Crawler.Frontend.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Listen on 8081 by default; override ASPNETCORE_URLS at runtime if needed.
ENV ASPNETCORE_URLS=http://+:8081 \
    DOTNET_EnableDiagnostics=0
RUN useradd --create-home --uid 1001 --shell /usr/sbin/nologin crawler
COPY --from=publish /app/publish .
EXPOSE 8081
USER crawler
ENTRYPOINT ["dotnet", "CalderaReport.Crawler.Frontend.dll"]
