# syntax=docker/dockerfile:1
# Веб-інтерфейс: збірка Angular → nginx (без root) з проксі /api, /mcp, /webhook на Api, SPA-fallback, gzip.
# Збірка: docker build -f docker/web.Dockerfile .
FROM node:24-alpine AS build
WORKDIR /web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN --mount=type=cache,id=npm,target=/root/.npm npm ci --no-audit --no-fund
COPY apps/web/ ./
RUN npx ng build --configuration production

FROM nginxinc/nginx-unprivileged:1.29-alpine
# Адреса Api всередині мережі Compose; шаблон підставляється при старті (envsubst образу nginx).
ENV OPENBP_API_UPSTREAM=api:8080 \
    OPENBP_DNS_RESOLVER=127.0.0.11
COPY docker/web/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/dist/web/browser/ /usr/share/nginx/html/
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/nginx-health >/dev/null || exit 1
