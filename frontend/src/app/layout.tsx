import './globals.css';
import type { Metadata } from 'next';
import Topbar from '@/components/topbar/Topbar';
import { TopbarProvider } from '@/components/topbar/TopbarContext';
import Sidebar from '@/components/Sidebar';
import React from 'react';

export const metadata: Metadata = {
  title: 'Admin | Kirma.sh',
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="ru">
      <body className="h-screen">
        <TopbarProvider>
          <div className="flex h-screen">
            <Sidebar />
            <div className="flex-1 flex flex-col overflow-hidden">
              <Topbar />
              <main className="flex-1 overflow-auto bg-gray-50 p-4">
                {children}
              </main>
            </div>
          </div>
        </TopbarProvider>
      </body>
    </html>
  );
}
