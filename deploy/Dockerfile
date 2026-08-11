FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY app/CSharpHomeAssistant.csproj app/
RUN dotnet restore app/CSharpHomeAssistant.csproj

COPY app/ app/
RUN dotnet publish app/CSharpHomeAssistant.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /out/ .
USER $APP_UID
ENTRYPOINT ["dotnet", "CSharpHomeAssistant.dll"]
