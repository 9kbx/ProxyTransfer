FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV TunnelHost__ApiUrl=http://0.0.0.0:8080

COPY . .

EXPOSE 8080

ENTRYPOINT ["dotnet", "ProxyTransfer.Api.dll"]