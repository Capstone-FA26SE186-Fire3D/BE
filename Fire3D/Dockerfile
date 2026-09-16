# ──────────────────────────────────────────────────────────────
# Stage 1: Build
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + project files trước để tận dụng Docker layer cache
COPY Fire3D.slnx ./
COPY Fire3D.API/Fire3D.API.csproj              Fire3D.API/
COPY Fire3D.Application/Fire3D.Application.csproj Fire3D.Application/
COPY Fire3D.Infrastructure/Fire3D.Infrastructure.csproj Fire3D.Infrastructure/
COPY Fire3D.Domain/Fire3D.Domain.csproj        Fire3D.Domain/
COPY Fire3D.AuthTests/Fire3D.AuthTests.csproj  Fire3D.AuthTests/

RUN dotnet restore Fire3D.slnx

# Copy toàn bộ source
COPY . .

# Publish API project (Release, self-contained=false, dùng runtime image)
RUN dotnet publish Fire3D.API/Fire3D.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ──────────────────────────────────────────────────────────────
# Stage 2: Runtime
# ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Tạo user non-root để tăng bảo mật
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

# Render inject biến PORT (thường là 10000). ASPNETCORE_URLS đọc từ env.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Fire3D.API.dll"]
