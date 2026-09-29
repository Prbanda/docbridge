import type { APIGatewayProxyResultV2, APIGatewayProxyWebsocketEventV2 } from 'aws-lambda';

/**
 * M0 stub for the WebSocket lifecycle handlers (built out in M3, ADR-007):
 *   $connect    — validate JWT from query param, insert ws_connections row
 *   subscribe   — authorize job view, set job_id on the connection row
 *   $disconnect — delete the connection row
 */
export async function handler(
  event: APIGatewayProxyWebsocketEventV2,
): Promise<APIGatewayProxyResultV2> {
  const routeKey = event.requestContext.routeKey;
  console.log(JSON.stringify({ msg: 'ws stub', routeKey, connectionId: event.requestContext.connectionId }));
  return { statusCode: 200 };
}
