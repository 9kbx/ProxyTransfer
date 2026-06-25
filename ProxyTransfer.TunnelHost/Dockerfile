FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV TunnelHost__ManagementUrl=http://0.0.0.0:5081

COPY . .

EXPOSE 5081

ENTRYPOINT ["dotnet", "ProxyTransfer.TunnelHost.dll"]
