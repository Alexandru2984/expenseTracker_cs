# Build stage
FROM node:24-alpine AS build
ARG VCS_REF=unknown
ENV BUILD_REVISION=$VCS_REF
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts

COPY . ./
RUN npm run build

# Distribution stage — thin Alpine image holding compiled assets.
# In production, docker-compose uses this as a one-shot init container to
# populate the Nginx static directory (see deploy.md).
FROM alpine:3.24
COPY --from=build /app/dist /app/dist
CMD ["sh", "-c", "cp -r /app/dist/. ${DEST:-/srv} && echo 'UI assets copied.'"]

