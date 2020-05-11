# NuGet restore
FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build
WORKDIR /src
COPY *.sln .
COPY APICore.API/*.csproj APICore.API/
COPY APICore.Common/*.csproj APICore.Common/
COPY APICore.Data/*.csproj APICore.Data/
COPY APICore.Services/*.csproj APICore.Services/

RUN dotnet restore
COPY . .

# build
WORKDIR /src/APICore.API
RUN dotnet build
WORKDIR /src/APICore.Common
RUN dotnet build
WORKDIR /src/APICore.Data
RUN dotnet build
WORKDIR /src/APICore.Services
RUN dotnet build

# publish
FROM build AS publish
WORKDIR /src/APICore.API
RUN dotnet publish -c Release -o /src/publish

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1 AS runtime
WORKDIR /app
COPY --from=publish /src/publish .
CMD ASPNETCORE_URLS=http://*:$PORT dotnet APICore.API.dll