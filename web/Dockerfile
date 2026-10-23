# Build context is web/. Stage 1 builds the static SPA; stage 2 serves it with nginx (non-root).
# Node 24 is the current LTS line (doctor.sh requires Node 22+).
FROM node:24-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:stable-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/build/client/ /usr/share/nginx/html/
EXPOSE 8080
