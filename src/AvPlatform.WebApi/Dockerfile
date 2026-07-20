FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AvPlatform.WebApi/AvPlatform.WebApi.csproj src/AvPlatform.WebApi/
RUN dotnet restore src/AvPlatform.WebApi/AvPlatform.WebApi.csproj
COPY . .
RUN dotnet publish src/AvPlatform.WebApi/AvPlatform.WebApi.csproj -c Release -o /app/publish /p:UseAppHost=false
RUN mkdir -p /curl-runtime/bin /curl-runtime/lib \
    && cp /usr/bin/curl /curl-runtime/bin/ \
    && ldd /usr/bin/curl \
      | awk '/=> \// { print $3 } /^\// { print $1 }' \
      | xargs -r -I '{}' cp '{}' /curl-runtime/lib/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /curl-runtime/ /opt/curl/
ENV PATH="/opt/curl/bin:${PATH}" \
    LD_LIBRARY_PATH="/opt/curl/lib"
COPY --from=build /app/publish .
RUN mkdir -p /app/data/logs && chown -R $APP_UID:$APP_UID /app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AvPlatform.WebApi.dll"]
