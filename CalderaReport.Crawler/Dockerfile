FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
ARG BUILD_CONFIGURATION=Release
COPY ["CalderaReport.Crawler/CalderaReport.Crawler.csproj", "CalderaReport.Crawler/"]
COPY ["CalderaReport.Services/CalderaReport.Services.csproj", "CalderaReport.Services/"]
COPY ["CalderaReport.Clients/CalderaReport.Clients.csproj", "CalderaReport.Clients/"]
COPY ["CalderaReport.Domain/CalderaReport.Domain.csproj", "CalderaReport.Domain/"]
RUN dotnet restore "CalderaReport.Crawler/CalderaReport.Crawler.csproj"

FROM restore AS publish
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR /src/CalderaReport.Crawler
RUN dotnet publish "CalderaReport.Crawler.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
RUN useradd --create-home --uid 1001 --shell /usr/sbin/nologin crawler
COPY --from=publish /app/publish .
USER crawler
ENTRYPOINT ["dotnet", "CalderaReport.Crawler.dll"]
