FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Hina.Core/ Hina.Core/
COPY Hina.Host/ Hina.Host/
RUN dotnet publish Hina.Host/Hina.Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
VOLUME /app/patch
ENV ASPNETCORE_URLS=http://+:49876
EXPOSE 49876
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD wget -qO- http://localhost:49876/health || exit 1
ENTRYPOINT ["dotnet", "Hina.Host.dll"]
