FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AccessibilityMap/*.csproj ./AccessibilityMap/
RUN dotnet restore ./AccessibilityMap/KyivAccessibilityMap.csproj
COPY . .
RUN dotnet publish ./AccessibilityMap/KyivAccessibilityMap.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "KyivAccessibilityMap.dll"]
