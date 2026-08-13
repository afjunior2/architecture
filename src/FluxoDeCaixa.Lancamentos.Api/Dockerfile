FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY src/FluxoDeCaixa.Contracts/ src/FluxoDeCaixa.Contracts/
COPY src/FluxoDeCaixa.Lancamentos.Domain/ src/FluxoDeCaixa.Lancamentos.Domain/
COPY src/FluxoDeCaixa.Lancamentos.Application/ src/FluxoDeCaixa.Lancamentos.Application/
COPY src/FluxoDeCaixa.Lancamentos.Infrastructure/ src/FluxoDeCaixa.Lancamentos.Infrastructure/
COPY src/FluxoDeCaixa.Lancamentos.Api/ src/FluxoDeCaixa.Lancamentos.Api/
RUN dotnet publish src/FluxoDeCaixa.Lancamentos.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FluxoDeCaixa.Lancamentos.Api.dll"]
