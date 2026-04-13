FROM node:20-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .
RUN npm run build

FROM node:20-alpine AS runtime
WORKDIR /app

RUN npm install -g serve@14 --no-audit --no-fund

COPY --from=build /app/dist ./dist

ENV PORT=4173
EXPOSE 4173
CMD ["sh", "-c", "serve -l ${PORT} dist"]
