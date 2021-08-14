FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /app

COPY app/CSharpHomeAssistant.sln ./
COPY app/CSharpHomeAssistant.csproj ./

RUN dotnet restore


COPY app ./
RUN dotnet publish -c Release -o out


# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine AS final
WORKDIR /app
RUN mkdir -p /app/Apps
COPY --from=build /app/out . 
ENTRYPOINT ["dotnet", "CSharpHomeAssistant.dll"]
