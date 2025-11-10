# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY ./src ./src
RUN dotnet restore ./src/App/App.fsproj

# publish
RUN dotnet publish ./src/App/App.fsproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /app/publish/ ./

# Suave will read PORT; default to 8080
ENV PORT=8080 MODE=web
EXPOSE 8080

ENTRYPOINT ["dotnet", "ArbitrageGainer.dll"]