import './globals.css';
import type { Metadata } from 'next';
import Topbar from '@/components/topbar/Topbar';
import { TopbarProvider } from '@/components/topbar/TopbarContext';
import Sidebar from '@/components/Sidebar';
import React from 'react';
import { Inter } from 'next/font/google';

const inter = Inter({
  subsets: ['latin', 'cyrillic', 'cyrillic-ext'],
  display: 'swap',
});

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
      <body className={`${inter.className} h-screen antialiased text-gray-900`}>
        <TopbarProvider>
          <div className="flex h-screen">
            <Sidebar />
            <div className="flex-1 flex flex-col overflow-hidden">
              <Topbar />
              <main className="flex-1 overflow-auto bg-gray-50/80 p-6">
                {children}
              </main>
            </div>
          </div>
        </TopbarProvider>
      </body>
    </html>
  );
}
