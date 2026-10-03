FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
# Lockfile is produced on Windows, so npm ci on Alpine skips the musl
# native binaries Tailwind needs at build time.
RUN npm ci \
  && npm install --no-save --include=optional \
    lightningcss-linux-x64-musl@1.29.2 \
    @tailwindcss/oxide-linux-x64-musl@4.1.4

COPY . .

ARG NEXT_PUBLIC_SHOP_DOMAIN
ENV NEXT_PUBLIC_SHOP_DOMAIN=$NEXT_PUBLIC_SHOP_DOMAIN

ARG API_PROXY_TARGET=http://backend:8080
ENV API_PROXY_TARGET=$API_PROXY_TARGET

RUN npm run build

FROM node:22-alpine
WORKDIR /app

ENV NODE_ENV=production

COPY package.json package-lock.json ./
RUN npm ci --omit=dev

COPY --from=build /app/.next-runtime ./.next-runtime
COPY --from=build /app/public ./public
COPY --from=build /app/next.config.ts ./

EXPOSE 3000
CMD ["npm", "start"]
