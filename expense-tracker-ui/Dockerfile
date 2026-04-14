# Build stage
FROM node:20-alpine AS build
WORKDIR /app

COPY package.json package-lock.json* ./
RUN npm ci

COPY . ./
RUN npm run build

# Distribution stage — thin Alpine image holding compiled assets.
# In production, docker-compose uses this as a one-shot init container to
# populate the Caddy file-serving volume (see deploy.md).
FROM alpine:3.19
COPY --from=build /app/dist /app/dist
CMD ["sh", "-c", "cp -r /app/dist/. ${DEST:-/srv} && echo 'UI assets copied.'"]

