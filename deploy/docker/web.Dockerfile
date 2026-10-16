# AML Platform web UI (React/Vite), served by nginx which also proxies /api to the API container.
ARG NODE_IMAGE=node:22-alpine
ARG NGINX_IMAGE=nginx:1.27-alpine
FROM ${NODE_IMAGE} AS build
WORKDIR /web
COPY src/AML.Web/package.json src/AML.Web/package-lock.json ./
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
RUN npm ci --no-audit --no-fund
COPY src/AML.Web/ ./
RUN npm run build

FROM ${NGINX_IMAGE}
COPY deploy/docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 80
