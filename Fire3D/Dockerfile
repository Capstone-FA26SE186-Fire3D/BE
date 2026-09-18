# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Context đang là thư mục root của repo (BE/)
COPY . .

# Di chuyển vào thư mục code và build
WORKDIR /src/Fire3D
RUN dotnet restore Fire3D.API/Fire3D.API.csproj
RUN dotnet publish Fire3D.API/Fire3D.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Sử dụng non-root user mặc định ('app') có sẵn từ .NET 8 trở lên
USER $APP_UID

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Fire3D.API.dll"]
