import {
  type DiscoveryService,
  type MetadataScanner,
  type ModuleRef,
} from '@nestjs/core';
import { type InstanceWrapper } from '@nestjs/core/injector/instance-wrapper';
import { type Module } from '@nestjs/core/injector/module';

import { type ExceptionHandlerService } from 'src/engine/core-modules/exception-handler/exception-handler.service';
import { MessageQueueExplorer } from 'src/engine/core-modules/message-queue/message-queue.explorer';
import { type MessageQueueMetadataAccessor } from 'src/engine/core-modules/message-queue/message-queue-metadata.accessor';
import { MessageQueue } from 'src/engine/core-modules/message-queue/message-queue.constants';
import { type MessageQueueService } from 'src/engine/core-modules/message-queue/services/message-queue.service';

describe('MessageQueueExplorer', () => {
  it('should await queue worker registration during module initialization', async () => {
    let resolveRegistration: () => void = () => undefined;
    const registrationPromise = new Promise<void>((resolve) => {
      resolveRegistration = resolve;
    });
    const queueWork = jest.fn().mockReturnValue(registrationPromise);
    const queueService = {
      work: queueWork,
    } as unknown as MessageQueueService;
    const processorInstance = { process: jest.fn() };
    const processorWrapper = {
      host: {} as Module,
      inject: undefined,
      instance: processorInstance,
      isDependencyTreeStatic: () => true,
      metatype: processorInstance.constructor,
      name: 'TestProcessor',
    } as unknown as InstanceWrapper;
    const moduleRef = {
      get: jest.fn().mockReturnValue(queueService),
    } as unknown as ModuleRef;
    const discoveryService = {
      getProviders: jest.fn().mockReturnValue([processorWrapper]),
    } as unknown as DiscoveryService;
    const metadataAccessor = {
      getProcessorMetadata: jest
        .fn()
        .mockReturnValue({ queueName: MessageQueue.taskAssignedQueue }),
      isProcess: jest.fn().mockReturnValue(true),
      isProcessor: jest.fn().mockReturnValue(true),
    } as unknown as MessageQueueMetadataAccessor;
    const metadataScanner = {
      getAllMethodNames: jest.fn().mockReturnValue(['process']),
    } as unknown as MetadataScanner;
    const exceptionHandlerService = {} as ExceptionHandlerService;
    const explorer = new MessageQueueExplorer(
      moduleRef,
      discoveryService,
      metadataAccessor,
      metadataScanner,
      exceptionHandlerService,
    );

    const initializationPromise = explorer.onModuleInit();

    expect(initializationPromise).toBeInstanceOf(Promise);

    let isInitialized = false;

    void Promise.resolve(initializationPromise).then(() => {
      isInitialized = true;
    });
    await Promise.resolve();

    expect(isInitialized).toBe(false);

    resolveRegistration();
    await initializationPromise;

    expect(queueWork).toHaveBeenCalledTimes(1);
  });
});
