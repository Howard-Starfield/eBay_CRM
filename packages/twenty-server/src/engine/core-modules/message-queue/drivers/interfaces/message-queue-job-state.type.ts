export type MessageQueueJobState =
  | 'created'
  | 'active'
  | 'completed'
  | 'failed'
  | 'retry';
