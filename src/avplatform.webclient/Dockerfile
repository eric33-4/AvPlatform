FROM node:24.12-alpine AS build
WORKDIR /app
COPY src/avplatform.webclient/package*.json ./
RUN npm install --global npm@11.6.2 && npm ci
COPY src/avplatform.webclient/ ./
RUN npm run build

FROM nginx:1.29-alpine AS final
COPY src/avplatform.webclient/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
