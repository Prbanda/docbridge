# Build context is the repo root (monorepo workspaces).
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json tsconfig.base.json ./
COPY packages/shared/package.json packages/shared/
COPY services/auth/package.json services/auth/
COPY services/platform/package.json services/platform/
COPY services/docbridge-api/package.json services/docbridge-api/
COPY workers/fanout/package.json workers/fanout/
COPY workers/delivery/package.json workers/delivery/
COPY workers/ws/package.json workers/ws/
RUN npm ci
COPY packages/shared packages/shared
COPY services/platform services/platform
RUN npm run build -w @docbridge/shared -w @docbridge/platform

FROM node:22-alpine
ENV NODE_ENV=production
WORKDIR /app
COPY --from=build /app/package.json /app/package-lock.json ./
COPY --from=build /app/packages/shared/package.json packages/shared/
COPY --from=build /app/services/platform/package.json services/platform/
RUN npm ci --omit=dev --workspace @docbridge/shared --workspace @docbridge/platform
COPY --from=build /app/packages/shared/dist packages/shared/dist
COPY --from=build /app/services/platform/dist services/platform/dist
USER node
EXPOSE 3002
CMD ["node", "services/platform/dist/index.js"]
