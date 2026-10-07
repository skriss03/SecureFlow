FROM node:14 AS build
WORKDIR /src
COPY . .
RUN npm ci && npm run build -- --configuration production

FROM nginx:1.19
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/storefront-web /usr/share/nginx/html
