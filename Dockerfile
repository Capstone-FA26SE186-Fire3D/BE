FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Fire3D/ Fire3D/
RUN dotnet publish Fire3D/Fire3D.API/Fire3D.API.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Fire3D.API.dll"]
