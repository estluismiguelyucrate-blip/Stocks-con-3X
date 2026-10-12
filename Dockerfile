FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Stoxx.Web/Stoxx.Web.csproj src/Stoxx.Web/
RUN dotnet restore src/Stoxx.Web/Stoxx.Web.csproj

COPY . .
RUN dotnet publish src/Stoxx.Web/Stoxx.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "Stoxx.Web.dll"]
